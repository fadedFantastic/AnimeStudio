using Newtonsoft.Json;

namespace AnimeStudio.AssetExplorer;

public sealed record PrimaryExportResult(string[] MainFiles, int Animations = 0, string SkippedReason = null)
{
    public static PrimaryExportResult Skip(string reason) => new([], SkippedReason: reason);
}
public sealed record DirectoryExportItem(string ResourcePath, string Status, string[] MainFiles, int Animations, string Error);
public sealed record DirectoryExportResult(string OutputDirectory, int Succeeded, int Failed, int Pending, bool Cancelled,
    int Skipped = 0, string ReportPath = null);

public static class DirectoryExportService
{
    public static async Task<DirectoryExportResult> RunAsync(DirectoryExportPlan plan, string output,
        Func<PrimaryAssetGroup, string, CancellationToken, Task<PrimaryExportResult>> export,
        Action<string> progress, CancellationToken token, int maxConcurrency = 1, string logDirectory = null)
    {
        if (plan.Groups.Length == 0) throw new InvalidOperationException("没有勾选可导出的资源。");
        token.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(output);
        // Reports live with the application, never among the exported source assets.
        logDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeStudio", "AssetExplorer", "ExportLogs");
        Directory.CreateDirectory(logDirectory);
        var reportPath = Path.Combine(logDirectory, "export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var results = new List<DirectoryExportItem>();
        var sync = new object();
        using var publishGate = new SemaphoreSlim(1);
        var published = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        maxConcurrency = Math.Clamp(maxConcurrency, 1, 8);
        using var journal = new StreamWriter(Path.ChangeExtension(reportPath, ".jsonl")) { AutoFlush = true };
        void WriteManifest(string state)
        {
            File.WriteAllText(reportPath + ".tmp", JsonConvert.SerializeObject(new { plan.Directory, plan.Recursive, OutputDirectory = root,
                Total = plan.Groups.Length, State = state, Completed = results.Count, Items = results }, Formatting.Indented));
            File.Move(reportPath + ".tmp", reportPath, true);
        }
        WriteManifest("running");
        try
        {
            await Parallel.ForEachAsync(plan.Groups, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency }, async (group, _) =>
            {
                if (token.IsCancellationRequested) return;
                DirectoryExportItem item;
                var staging = Directory.CreateTempSubdirectory("aex-export-");
                try
                {
                    var result = await export(group, staging.FullName, token);
                    token.ThrowIfCancellationRequested();
                    if (result.SkippedReason != null)
                        item = new(group.ResourcePath, "skipped", [], 0, result.SkippedReason);
                    else
                    {
                        if (result.MainFiles.Length == 0 || result.MainFiles.Any(p => !IsFileWithin(staging.FullName, p) || new FileInfo(p).Length == 0))
                            throw new IOException("导出器未生成主资源文件。");
                        await publishGate.WaitAsync(token);
                        try { item = await PublishAsync(root, plan.Directory, group, staging.FullName, result, published, token); }
                        finally { publishGate.Release(); }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { item = new(group.ResourcePath, "cancelled", [], 0, "用户取消"); }
                catch (Exception ex)
                { item = new(group.ResourcePath, "failed", [], 0, ex.Message); }
                finally
                {
                    try { staging.Delete(true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                lock (sync)
                {
                    results.Add(item);
                    journal.WriteLine(JsonConvert.SerializeObject(item));
                    var state = item.Status switch { "success" => "成功", "skipped" => "跳过", "cancelled" => "取消", _ => "失败" };
                    progress?.Invoke($"已处理 {results.Count}/{plan.Groups.Length} · 并发 {maxConcurrency} · {state} · {group.ResourcePath}" +
                        (item.Error == null ? "" : " · " + item.Error));
                }
            });
        }
        finally { WriteManifest(token.IsCancellationRequested ? "cancelled" : results.Count < plan.Groups.Length ? "interrupted" : results.Any(r => r.Status != "success") ? "partial" : "complete"); }
        return new(root, results.Count(r => r.Status == "success"), results.Count(r => r.Status == "failed"),
            plan.Groups.Length - results.Count(r => r.Status is "success" or "failed" or "skipped"), token.IsCancellationRequested,
            results.Count(r => r.Status == "skipped"), reportPath);
    }

    private static bool IsFileWithin(string directory, string file) =>
        Path.GetFullPath(file).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
        File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0;

    private static async Task<DirectoryExportItem> PublishAsync(string root, string directory, PrimaryAssetGroup group,
        string staging, PrimaryExportResult result, Dictionary<string, string> published, CancellationToken token)
    {
        var folder = ResourcePaths.OutputDirectory(root, directory, group.ResourcePath);
        var mainFiles = result.MainFiles.Select(p => ResourcePaths.ContainedPath(folder, Path.GetRelativePath(staging, p))).ToArray();
        foreach (var main in mainFiles)
        {
            if (published.TryGetValue(main, out var owner)) throw new IOException($"输出文件重名：{main}，已由 {owner} 导出。");
            if (File.Exists(main)) return new(group.ResourcePath, "skipped", [], 0, "目标文件已存在，未覆盖：" + main);
        }
        var files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories)
            .Select(source => (Source: source, Target: ResourcePaths.ContainedPath(folder, Path.GetRelativePath(staging, source)))).ToArray();
        var copies = new List<(string Source, string Target)>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (!IsFileWithin(staging, file.Source)) throw new IOException("导出器生成了非法文件。");
            if (File.Exists(file.Target))
            {
                if (!await SameContentAsync(file.Source, file.Target, token)) throw new IOException("依赖文件内容冲突，未覆盖：" + file.Target);
            }
            else copies.Add(file);
        }

        // Copy beside the final files, then rename: this also works across volumes. On failure
        // roll back only this resource's new files, keeping every pre-existing file intact.
        var temporary = new List<(string Temp, string Target)>();
        var created = new List<string>();
        try
        {
            foreach (var file in copies)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Target));
                var temp = Path.Combine(Path.GetDirectoryName(file.Target), ".aex-" + Guid.NewGuid().ToString("N") + ".tmp");
                temporary.Add((temp, file.Target));
                await using var source = File.OpenRead(file.Source);
                await using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(target, token);
            }
            token.ThrowIfCancellationRequested();
            foreach (var file in temporary) { File.Move(file.Temp, file.Target, false); created.Add(file.Target); }
            foreach (var file in mainFiles) published.Add(file, group.ResourcePath);
            return new(group.ResourcePath, "success", mainFiles.Select(p => Path.GetRelativePath(root, p)).ToArray(), result.Animations, null);
        }
        catch { foreach (var file in created) File.Delete(file); throw; }
        finally { foreach (var file in temporary) if (File.Exists(file.Temp)) File.Delete(file.Temp); }
    }

    private static async Task<bool> SameContentAsync(string a, string b, CancellationToken token)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        await using var left = File.OpenRead(a); await using var right = File.OpenRead(b);
        var hashA = await System.Security.Cryptography.SHA256.HashDataAsync(left, token);
        var hashB = await System.Security.Cryptography.SHA256.HashDataAsync(right, token);
        return hashA.AsSpan().SequenceEqual(hashB);
    }
}
