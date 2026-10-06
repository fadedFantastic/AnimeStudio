using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace AnimeStudio.AssetExplorer;

public static class IndexWorker
{
    public sealed record Request(string ScanDirectory, string WorkDirectory, GameType Game, bool Full, string CancelFile, string ResultFile);
    public sealed record Result(string Catalog, int Scanned, int Reused, int Failed, string[] Errors);

    public static bool TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != "--asset-explorer-scan") return false;
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length != 2) throw new ArgumentException("--asset-explorer-scan requires a request file.");
            var request = JsonConvert.DeserializeObject<Request>(File.ReadAllText(args[1]));
            using var cts = new CancellationTokenSource();
            using var timer = new System.Threading.Timer(_ => { if (File.Exists(request.CancelFile)) cts.Cancel(); }, null, 0, 100);
            Logger.Silent = true;
            var result = Run(request, message => Console.WriteLine("AEX:" + message), cts.Token);
            File.WriteAllText(request.ResultFile, JsonConvert.SerializeObject(result));
            Environment.ExitCode = result.Failed == 0 ? 0 : 2;
        }
        catch (OperationCanceledException) { Console.WriteLine("AEX:已取消；下次继续使用已完成的 blk 缓存。"); Environment.ExitCode = 3; }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        return true;
    }

    public static Result Run(Request request, Action<string> report, CancellationToken token)
    {
        var root = Path.GetFullPath(request.ScanDirectory);
        var files = Directory.EnumerateFiles(root, "*.blk", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("所选目录没有 .blk 文件。");
        var cache = Path.Combine(request.WorkDirectory, "blocks-v1");
        Directory.CreateDirectory(cache);
        var builder = new AssetIndexStore.Builder();
        var cabs = new List<CabEntry>();
        var errors = new List<string>();
        int scanned = 0, reused = 0;
        for (var i = 0; i < files.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var file = files[i];
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.ToUpperInvariant())));
            var cached = Path.Combine(cache, key + ".json.gz");
            CatalogScanner.Shard shard = null;
            var info = new FileInfo(file);
            try
            {
                if (File.Exists(cached))
                {
                    using var fs = File.OpenRead(cached);
                    using var zip = new GZipStream(fs, CompressionMode.Decompress);
                    using var text = new StreamReader(zip);
                    using var reader = new JsonTextReader(text);
                    shard = new JsonSerializer().Deserialize<CatalogScanner.Shard>(reader);
                    if (shard.Length != info.Length || shard.Modified != info.LastWriteTimeUtc.Ticks || shard.Full != request.Full || shard.Game != request.Game) shard = null;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException) { shard = null; }
            if (shard == null)
            {
                try
                {
                    shard = CatalogScanner.Scan(file, request.Game, request.Full, token);
                    var temp = cached + ".tmp";
                    try
                    {
                        using (var fs = File.Create(temp))
                        using (var zip = new GZipStream(fs, CompressionLevel.Fastest))
                        using (var text = new StreamWriter(zip))
                        using (var writer = new JsonTextWriter(text)) new JsonSerializer().Serialize(writer, shard);
                        token.ThrowIfCancellationRequested(); File.Move(temp, cached, true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    scanned++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add(file + ": " + ex.Message); }
            }
            else reused++;
            if (shard != null)
            {
                foreach (var row in shard.Assets) builder.Add(row);
                cabs.AddRange(shard.Cabs);
            }
            if (i % 25 == 0 || i == files.Length - 1)
                report?.Invoke($"[{i + 1}/{files.Length}] 新扫 {scanned:N0} · 缓存 {reused:N0} · 失败 {errors.Count:N0} · 资源 {builder.Count:N0}");
        }
        token.ThrowIfCancellationRequested();
        if (builder.Count == 0) throw new InvalidDataException("没有读取到资源。" + string.Join(Environment.NewLine, errors.Take(3)));
        var generation = Path.Combine(request.WorkDirectory, "catalogs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(generation);
        var output = Path.Combine(generation, "catalog.aex");
        var store = builder.Build(request.Game, output);
        report?.Invoke("保存索引和 CAB 依赖…");
        store.Save(output, token);
        CabCatalog.Write(Path.ChangeExtension(output, ".bin"), root, cabs);
        File.WriteAllLines(Path.Combine(generation, "scan-errors.txt"), errors);
        token.ThrowIfCancellationRequested();
        // Publish only after both the asset index and dependency map are complete.
        File.WriteAllText(Path.Combine(request.WorkDirectory, "last-catalog.tmp"), output);
        File.Move(Path.Combine(request.WorkDirectory, "last-catalog.tmp"), Path.Combine(request.WorkDirectory, "last-catalog.txt"), true);
        return new Result(output, scanned, reused, errors.Count, errors.ToArray());
    }

    internal static async Task<Result> StartAsync(Request request, Action<string> report, CancellationToken token)
    {
        Directory.CreateDirectory(request.WorkDirectory);
        var requestPath = request.ResultFile + ".request.json";
        await File.WriteAllTextAsync(requestPath, JsonConvert.SerializeObject(request), token);
        var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8 };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly().Location);
        start.ArgumentList.Add("--asset-explorer-scan"); start.ArgumentList.Add(requestPath);
        using var process = Process.Start(start) ?? throw new IOException("无法启动索引进程。");
        using var registration = token.Register(() => File.WriteAllText(request.CancelFile, "cancel"));
        var stderr = process.StandardError.ReadToEndAsync();
        while (await process.StandardOutput.ReadLineAsync() is string line)
            if (line.StartsWith("AEX:")) report?.Invoke(line[4..]);
        await process.WaitForExitAsync();
        token.ThrowIfCancellationRequested();
        var error = await stderr;
        if (process.ExitCode is not (0 or 2) || !File.Exists(request.ResultFile))
            throw new InvalidOperationException("索引进程失败：" + error);
        return JsonConvert.DeserializeObject<Result>(await File.ReadAllTextAsync(request.ResultFile, token));
    }
}
