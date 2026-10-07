using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MessagePack;
using Newtonsoft.Json;

namespace AnimeStudio.AssetExplorer;

internal sealed class AssetIndexStore : IMeshCatalog
{
    public string[] Name, Type, Blk, Container, Source, Cab, Guessed;
    public long[] PathID, Offset;
    public int Count => Name.Length;
    public string SourcePath;
    public GameType Game = GameType.ZZZ;
    public string[] AllTypes;
    private Dictionary<string, int[]> typeRows;
    private const string Signature = "AnimeStudio.AssetExplorer/2";

    public string Display(int i) => Guessed?[i] is string guess ? "~" + guess : Container[i];
    public CatalogAsset Entry(int i) => new()
    {
        Name = Name[i], Type = Enum.Parse<ClassIDType>(Type[i]), Container = Container[i],
        Source = Source[i], Offset = Offset[i], PathID = PathID[i], Cab = Cab[i]
    };

    public static AssetIndexStore Load(string path, string cacheDirectory, Action<string> report, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        if (Path.GetExtension(path).Equals(".aex", StringComparison.OrdinalIgnoreCase)) return ReadBinary(path, token);
        var info = new FileInfo(path);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        var cache = Path.Combine(cacheDirectory, key + ".aex");
        var stamp = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        try
        {
            if (File.Exists(cache) && File.ReadAllText(cache + ".stamp") == stamp)
            { report?.Invoke("读取已缓存的索引…"); return ReadBinary(cache, token); }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException) { }

        report?.Invoke("首次导入清单，正在建立缓存…");
        var builder = new Builder();
        var game = GameType.ZZZ;
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".json":
                using (var stream = File.OpenRead(path))
                using (var text = new StreamReader(stream))
                using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None })
                {
                    var serializer = new JsonSerializer();
                    var found = false;
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (reader.TokenType != JsonToken.PropertyName) continue;
                        var property = (string)reader.Value;
                        if (property == "GameType")
                        { reader.Read(); game = Enum.Parse<GameType>(Convert.ToString(reader.Value, CultureInfo.InvariantCulture)); }
                        else if (property == "AssetEntries")
                        {
                            if (!reader.Read() || reader.TokenType != JsonToken.StartArray) throw new InvalidDataException("AssetEntries 必须是数组。");
                            found = true;
                            while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                            {
                                token.ThrowIfCancellationRequested();
                                if (reader.TokenType != JsonToken.StartObject) throw new InvalidDataException("清单条目格式错误。");
                                builder.Add(serializer.Deserialize<CatalogAsset>(reader));
                                if ((builder.Count & 0xFFFF) == 0) report?.Invoke($"导入 {builder.Count:N0} 条 · {stream.Position * 100 / Math.Max(1, stream.Length)}%");
                            }
                            if (reader.TokenType != JsonToken.EndArray) throw new InvalidDataException("清单不完整。");
                        }
                        else { reader.Read(); reader.Skip(); }
                    }
                    if (!found) throw new InvalidDataException("文件不包含 AssetEntries。");
                }
                break;
            case ".tsv":
                using (var reader = File.OpenText(path))
                {
                    var headers = (reader.ReadLine() ?? "").Split('\t');
                    var columns = headers.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
                    foreach (var required in new[] { "Name", "Type", "Source", "PathID", "Offset", "Container" })
                        if (!columns.ContainsKey(required)) throw new InvalidDataException("TSV 缺少列：" + required);
                    while (reader.ReadLine() is string line)
                    {
                        token.ThrowIfCancellationRequested();
                        var fields = line.Split('\t');
                        string Get(string name) => fields[columns[name]];
                        builder.Add(new CatalogAsset { Name = Get("Name"), Type = Enum.Parse<ClassIDType>(Get("Type")),
                            Container = Get("Container"), Source = Get("Source"), Offset = long.Parse(Get("Offset"), CultureInfo.InvariantCulture),
                            PathID = long.Parse(Get("PathID"), CultureInfo.InvariantCulture) });
                    }
                }
                break;
            case ".map":
                using (var stream = File.OpenRead(path))
                {
                    var map = MessagePackSerializer.Deserialize<AssetMap>(stream,
                        MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray), token);
                    game = map.GameType;
                    foreach (var a in map.AssetEntries)
                    {
                        token.ThrowIfCancellationRequested();
                        builder.Add(new CatalogAsset { Name = a.Name, Type = a.Type, Container = a.Container,
                            Source = a.Source, Offset = a.Offset, PathID = a.PathID });
                    }
                }
                break;
            default: throw new InvalidDataException("请选择 .json、.tsv、.map 或 .aex 清单。");
        }
        var result = builder.Build(game, path);
        result.Save(cache, token);
        File.WriteAllText(cache + ".stamp", stamp);
        return result;
    }

    internal sealed class Builder
    {
        private readonly List<string> names = [], types = [], containers = [], sources = [], cabs = [];
        private readonly List<long> ids = [], offsets = [];
        private readonly Dictionary<string, string> pool = new(StringComparer.Ordinal);
        public int Count => names.Count;
        private string Pool(string value)
        {
            value ??= "";
            if (pool.TryGetValue(value, out var existing)) return existing;
            pool.Add(value, value); return value;
        }
        public void Add(CatalogAsset a)
        {
            if (a == null || string.IsNullOrEmpty(a.Source)) throw new InvalidDataException("资源条目缺少 Source。");
            names.Add(a.Name ?? ""); types.Add(Pool(a.Type.ToString())); containers.Add(Pool(a.Container));
            sources.Add(Pool(a.Source)); cabs.Add(Pool(a.Cab)); ids.Add(a.PathID); offsets.Add(a.Offset);
        }
        public AssetIndexStore Build(GameType game, string path)
        {
            var store = new AssetIndexStore { Name = names.ToArray(), Type = types.ToArray(), Container = containers.ToArray(),
                Source = sources.ToArray(), Cab = cabs.ToArray(), PathID = ids.ToArray(), Offset = offsets.ToArray(), SourcePath = path, Game = game };
            store.Initialize(); return store;
        }
    }

    private void Initialize()
    {
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        Blk = new string[Count];
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < Count; i++)
        {
            if (!blocks.TryGetValue(Source[i], out var block)) blocks[Source[i]] = block = Path.GetFileName(Source[i]);
            Blk[i] = block;
            if (!groups.TryGetValue(Type[i], out var rows)) groups[Type[i]] = rows = [];
            rows.Add(i);
        }
        typeRows = groups.ToDictionary(x => x.Key, x => x.Value.ToArray());
        AllTypes = new[] { "全部类型" }.Concat(groups.Keys.OrderBy(x => x)).ToArray();
    }

    public void Save(string path, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temp))
            using (var zip = new GZipStream(stream, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(zip, Encoding.UTF8))
            {
                writer.Write(Signature); writer.Write(SourcePath ?? ""); writer.Write((int)Game); writer.Write(Count);
                var strings = new Dictionary<string, int>(StringComparer.Ordinal);
                void Write(string value)
                {
                    value ??= "";
                    if (strings.TryGetValue(value, out var id)) writer.Write(id);
                    else { writer.Write(~strings.Count); strings[value] = strings.Count; writer.Write(value); }
                }
                for (var i = 0; i < Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    writer.Write(Name[i]); Write(Type[i]); Write(Container[i]); Write(Source[i]); Write(Cab[i]);
                    writer.Write(PathID[i]); writer.Write(Offset[i]); writer.Write(Guessed?[i] ?? "");
                }
            }
            token.ThrowIfCancellationRequested(); File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static AssetIndexStore ReadBinary(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        using var zip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new BinaryReader(zip, Encoding.UTF8);
        if (reader.ReadString() != Signature) throw new InvalidDataException("索引版本不匹配，请重新导入清单。");
        var sourcePath = reader.ReadString(); var game = (GameType)reader.ReadInt32(); var count = reader.ReadInt32();
        if (count < 0 || count > 100_000_000) throw new InvalidDataException("无效的索引行数。");
        var store = new AssetIndexStore { Name = new string[count], Type = new string[count], Container = new string[count],
            Source = new string[count], Cab = new string[count], PathID = new long[count], Offset = new long[count],
            Guessed = new string[count], Game = game, SourcePath = sourcePath };
        var strings = new List<string>();
        string Read()
        {
            var id = reader.ReadInt32();
            if (id >= 0) return id < strings.Count ? strings[id] : throw new InvalidDataException("无效的字符串索引。");
            if (~id != strings.Count) throw new InvalidDataException("无效的字符串索引。");
            var value = reader.ReadString(); strings.Add(value); return value;
        }
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            store.Name[i] = reader.ReadString(); store.Type[i] = Read(); store.Container[i] = Read();
            store.Source[i] = Read(); store.Cab[i] = Read(); store.PathID[i] = reader.ReadInt64(); store.Offset[i] = reader.ReadInt64();
            var guess = reader.ReadString(); store.Guessed[i] = guess.Length == 0 ? null : guess;
        }
        store.Initialize(); return store;
    }

    public int ApplyPathDict(IReadOnlyDictionary<ulong, string> paths)
    {
        var count = 0;
        for (var i = 0; i < Count; i++)
            if (ulong.TryParse(Container[i], out var hash) && paths.TryGetValue(hash, out var path))
            { Container[i] = path; if (Guessed != null) Guessed[i] = null; count++; }
        return count;
    }

    public sealed record Query(string Text, string Type, string Blk, bool UseRegex);
    public DirectoryExportPlan CollectDirectory(string directory, bool recursive, CancellationToken token)
    {
        directory = ResourcePaths.Normalize(directory);
        var groups = new Dictionary<string, List<CatalogAsset>>(StringComparer.OrdinalIgnoreCase);
        var matched = 0;
        for (var i = 0; i < Count; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            var container = Container[i].Replace('\\', '/');
            if (!ResourcePaths.IsWithin(container, directory, recursive)) continue;
            container = ResourcePaths.Normalize(container);
            if (!groups.TryGetValue(container, out var members)) groups[container] = members = [];
            members.Add(Entry(i) with { Container = container });
            matched++;
        }
        var result = new List<PrimaryAssetGroup>();
        var unique = 0;
        foreach (var group in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            // Prefer one complete physical copy, but preserve unique subassets spread across files.
            var copies = group.Value.GroupBy(a => (a.Source.ToUpperInvariant(), a.Offset))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Item1, StringComparer.Ordinal).ThenBy(g => g.Key.Offset);
            var members = copies.SelectMany(g => g).DistinctBy(a => (a.Type, a.PathID, a.Name)).ToArray();
            unique += members.Length;
            result.Add(new PrimaryAssetGroup(group.Key, members));
        }
        return new DirectoryExportPlan(directory, recursive, result.ToArray(), matched, matched - unique);
    }

    public IReadOnlyDictionary<string, CatalogAsset[]> FindMeshes(IEnumerable<string> paths, CancellationToken token)
    {
        var requested = paths.Select(p => p.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var matches = requested.ToDictionary(p => p, _ => new List<CatalogAsset>(), StringComparer.OrdinalIgnoreCase);
        var hashes = requested.ToDictionary(PathRecovery.HashOf, p => p);
        if (typeRows.TryGetValue(nameof(ClassIDType.Mesh), out var rows))
            foreach (var i in rows)
            {
                token.ThrowIfCancellationRequested();
                var container = Container[i].Replace('\\', '/');
                if (matches.TryGetValue(container, out var list)) list.Add(Entry(i));
                else if (ulong.TryParse(container, out var hash) && hashes.TryGetValue(hash, out var path)) matches[path].Add(Entry(i));
            }
        return matches.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public int[] Search(Query query, CancellationToken token)
    {
        var text = query.Text?.Trim() ?? ""; var blk = query.Blk?.Trim() ?? "";
        int[] candidates = null;
        if (!string.IsNullOrEmpty(query.Type) && query.Type != "全部类型" && !typeRows.TryGetValue(query.Type, out candidates)) return [];
        var count = candidates?.Length ?? Count;
        var regex = query.UseRegex && text.Length > 0 ? new Regex(text, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)) : null;
        var blkRegex = query.UseRegex && blk.Length > 0 ? new Regex(blk, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)) : null;
        if (text.Length == 0 && blk.Length == 0)
        { token.ThrowIfCancellationRequested(); return candidates ?? Enumerable.Range(0, Count).ToArray(); }
        var workers = Math.Min(8, Math.Max(1, Math.Min(Environment.ProcessorCount - 1, (count + 32767) / 32768)));
        var parts = new List<int>[workers];
        var clock = Stopwatch.StartNew();
        Parallel.For(0, workers, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = workers }, w =>
        {
            var hits = new List<int>();
            for (var j = count * (long)w / workers; j < count * (long)(w + 1) / workers; j++)
            {
                if ((j & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (query.UseRegex && clock.Elapsed.TotalSeconds > 5) throw new TimeoutException("正则搜索超过 5 秒，请缩小范围或使用普通文本。");
                }
                var i = candidates == null ? (int)j : candidates[j];
                if (blk.Length > 0 && !(blkRegex?.IsMatch(Blk[i]) ?? Blk[i].Contains(blk, StringComparison.OrdinalIgnoreCase))) continue;
                bool Match(string value) => value != null && (regex?.IsMatch(value) ?? value.Contains(text, StringComparison.OrdinalIgnoreCase));
                if (text.Length == 0 || Match(Name[i]) || Match(Container[i]) || Match(Blk[i]) || Match(Guessed?[i])) hits.Add(i);
            }
            parts[w] = hits;
        });
        return parts.SelectMany(x => x).ToArray();
    }
}
