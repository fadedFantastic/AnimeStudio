namespace AnimeStudio.AssetExplorer;

public sealed record CabEntry(string Name, string Source, long Offset, string[] Dependencies);
public sealed record LoadPlan(CatalogAsset[] Selected, string[] Files, AssetsManager.AssetFilterDataItem[] Offsets, string[] Missing);

public sealed class CabCatalog
{
    public string Root { get; private set; }
    public List<CabEntry> Entries { get; } = [];

    public static CabCatalog Read(string path, CancellationToken token)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        var catalog = new CabCatalog { Root = reader.ReadString() };
        var count = reader.ReadInt32();
        if (count < 0 || count > 20_000_000) throw new InvalidDataException("CABMap 的条目数无效。");
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var name = reader.ReadString(); var source = reader.ReadString(); var offset = reader.ReadInt64();
            var depCount = reader.ReadInt32();
            if (depCount < 0 || depCount > 1_000_000) throw new InvalidDataException("CABMap 的依赖数无效。");
            var deps = new string[depCount];
            for (var j = 0; j < deps.Length; j++) deps[j] = reader.ReadString();
            catalog.Entries.Add(new CabEntry(name, Path.GetFullPath(Path.Combine(catalog.Root, source)), offset, deps));
        }
        return catalog;
    }

    public static void Write(string path, string root, IEnumerable<CabEntry> entries)
    {
        // Match the upstream CABMap format: a CAB name has one canonical location.
        // .aex rows retain the physical source/offset for every copy.
        var list = entries.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(root); writer.Write(list.Length);
        foreach (var cab in list)
        {
            writer.Write(cab.Name); writer.Write(Path.GetRelativePath(root, cab.Source)); writer.Write(cab.Offset);
            writer.Write(cab.Dependencies.Length);
            foreach (var dep in cab.Dependencies) writer.Write(dep);
        }
    }

    public static LoadPlan Plan(CatalogRequest request, CancellationToken token)
    {
        if (request.Assets.Length == 0) throw new InvalidOperationException("请先选择资源。");
        CabCatalog map = null;
        if (!string.IsNullOrWhiteSpace(request.CabMap)) map = Read(request.CabMap, token);
        string Resolve(string source)
        {
            if (string.IsNullOrWhiteSpace(request.SourceRoot)) return Path.GetFullPath(source);
            var relative = map == null ? Path.GetFileName(source) : Path.GetRelativePath(map.Root, source);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new InvalidDataException("资源不属于 CABMap 的根目录：" + source);
            return Path.GetFullPath(Path.Combine(request.SourceRoot, relative));
        }
        var selected = request.Assets.Select(a => a with { Source = Resolve(a.Source) }).ToArray();
        var offsets = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        void Add(string source, long offset)
        {
            if (!offsets.TryGetValue(source, out var set)) offsets[source] = set = [];
            set.Add(offset);
        }
        foreach (var row in selected) Add(row.Source, row.Offset);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (map != null)
        {
            var names = map.Entries.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
            var locations = map.Entries.GroupBy(x => Resolve(x.Source), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<CabEntry>();
            var preferred = new Dictionary<string, CabEntry>(StringComparer.OrdinalIgnoreCase);
            void Prefer(CabEntry entry)
            {
                if (preferred.TryGetValue(entry.Name, out var previous) &&
                    (!Resolve(previous.Source).Equals(Resolve(entry.Source), StringComparison.OrdinalIgnoreCase) || previous.Offset != entry.Offset))
                    throw new InvalidOperationException("所选资源来自同一 CAB 的不同副本，请只选择一份：" + entry.Name);
                preferred[entry.Name] = entry;
                queue.Enqueue(entry);
            }
            for (var selectedIndex = 0; selectedIndex < selected.Length; selectedIndex++)
            {
                token.ThrowIfCancellationRequested();
                var row = selected[selectedIndex];
                var cabs = locations.GetValueOrDefault(row.Source) ?? [];
                var matches = cabs.Where(c => (row.Offset < 0 || c.Offset == row.Offset) &&
                    (string.IsNullOrEmpty(row.Cab) || c.Name.Equals(row.Cab, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (matches.Length == 0)
                {
                    // Legacy maps keep only the first copy of a CAB. Read the selected
                    // bundle's headers to resolve its own dependencies, without reading objects.
                    var original = request.Assets[selectedIndex].Source;
                    matches = ReadSelectedHeaders(row, original, request.Game, token);
                }
                foreach (var cab in matches) Prefer(cab);
            }
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (queue.TryDequeue(out var cab))
            {
                token.ThrowIfCancellationRequested();
                if (!visited.Add(cab.Name)) continue;
                Add(Resolve(cab.Source), cab.Offset);
                foreach (var dep in cab.Dependencies)
                {
                    if (preferred.TryGetValue(dep, out var chosen)) queue.Enqueue(chosen);
                    else if (names.TryGetValue(dep, out var candidates))
                    {
                        chosen = candidates.FirstOrDefault(c => c.Source.Equals(cab.Source, StringComparison.OrdinalIgnoreCase)) ?? candidates[0];
                        preferred[dep] = chosen;
                        queue.Enqueue(chosen);
                    }
                    else if (!dep.StartsWith("unity", StringComparison.OrdinalIgnoreCase)) missing.Add("缺少 CAB " + dep);
                }
            }
        }
        foreach (var source in offsets.Keys) if (!File.Exists(source)) missing.Add("找不到文件 " + source);
        var filters = offsets.SelectMany(pair => (pair.Value.Contains(-1) ? new[] { -1L } : pair.Value.AsEnumerable())
            .Select(offset => new AssetsManager.AssetFilterDataItem { Source = pair.Key, Offset = offset })).ToArray();
        return new LoadPlan(selected, offsets.Keys.ToArray(), filters, missing.ToArray());
    }

    private static CabEntry[] ReadSelectedHeaders(CatalogAsset row, string originalSource, GameType game, CancellationToken token)
    {
        var manager = new AssetsManager { Game = GameManager.GetGameByType(game), SkipProcess = true, ResolveDependencies = false,
            FilterData = new() { Items = [new() { Source = row.Source, Offset = row.Offset }] } };
        try
        {
            using (token.Register(() => manager.tokenSource.Cancel())) manager.LoadFiles(new[] { row.Source }, mergeSplitAssets: false);
            token.ThrowIfCancellationRequested();
            var entries = manager.assetsFileList.Where(f => (row.Offset < 0 || f.offset == row.Offset) &&
                (string.IsNullOrEmpty(row.Cab) ? f.m_Objects.Any(o => o.m_PathID == row.PathID) : f.fileName.Equals(row.Cab, StringComparison.OrdinalIgnoreCase)))
                .Select(f => new CabEntry(f.fileName, originalSource, f.offset, f.m_Externals.Select(x => x.fileName).ToArray())).ToArray();
            if (entries.Length == 0) throw new InvalidDataException($"无法定位 {row.Name} 所在的 CAB，请更新清单。");
            return entries;
        }
        finally { manager.Clear(); }
    }

    public static AnimeStudio.Object Find(AssetsManager manager, CatalogAsset asset)
    {
        var matches = manager.assetsFileList.Where(file =>
                Path.GetFullPath(file.originalPath ?? file.fullName).Equals(Path.GetFullPath(asset.Source), StringComparison.OrdinalIgnoreCase) &&
                (asset.Offset < 0 || file.offset == asset.Offset) &&
                (string.IsNullOrEmpty(asset.Cab) || file.fileName.Equals(asset.Cab, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(file => file.Objects).Where(obj => obj.m_PathID == asset.PathID && obj.type == asset.Type).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"{asset.Name}：匹配到 {matches.Length} 个对象，请更新索引（PathID {asset.PathID}）。");
        return matches[0];
    }
}
