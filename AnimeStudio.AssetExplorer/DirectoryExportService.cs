using Newtonsoft.Json;

namespace AnimeStudio.AssetExplorer;

public sealed record PrimaryExportResult(string[] MainFiles, int Animations = 0);
public sealed record DirectoryExportItem(string ResourcePath, string Status, string[] MainFiles, int Animations, string Error);
public sealed record DirectoryExportResult(string OutputDirectory, int Succeeded, int Failed, int Pending, bool Cancelled);

public static class DirectoryExportService
{
    public static async Task<DirectoryExportResult> RunAsync(DirectoryExportPlan plan, string output,
        Func<PrimaryAssetGroup, string, CancellationToken, Task<PrimaryExportResult>> export,
        Action<string> progress, CancellationToken token, int maxConcurrency = 1)
    {
        if (plan.Groups.Length == 0) throw new InvalidOperationException("目录中没有可导出的资源。");
        token.ThrowIfCancellationRequested();
        var root = Path.Combine(Path.GetFullPath(output), "directory-export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var results = new List<DirectoryExportItem>();
        var cancelled = false;
        var sync = new object();
        maxConcurrency = Math.Clamp(maxConcurrency, 1, 8);
        using var journal = new StreamWriter(Path.Combine(root, "results.jsonl")) { AutoFlush = true };
        void WriteManifest(string state)
        {
            var path = Path.Combine(root, "export-manifest.json");
            File.WriteAllText(path + ".tmp", JsonConvert.SerializeObject(new { plan.Directory, plan.Recursive,
                Total = plan.Groups.Length, State = state, Completed = results.Count, Items = results }, Formatting.Indented));
            File.Move(path + ".tmp", path, true);
        }
        WriteManifest("running");
        try
        {
            await Parallel.ForEachAsync(plan.Groups, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency }, async (group, _) =>
            {
                if (token.IsCancellationRequested) return;
                var folder = ResourcePaths.OutputDirectory(root, plan.Directory, group.ResourcePath);
                Directory.CreateDirectory(folder);
                var marker = Path.Combine(folder, "export-incomplete.txt");
                File.WriteAllText(marker, "导出尚未完成。");
                DirectoryExportItem item;
                try
                {
                    var result = await export(group, folder, token);
                    if (result.MainFiles.Length == 0 || result.MainFiles.Any(p =>
                        !Path.GetFullPath(p).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(p) || new FileInfo(p).Length == 0))
                        throw new IOException("导出器未生成主资源文件。");
                    token.ThrowIfCancellationRequested();
                    item = new(group.ResourcePath, "success", result.MainFiles.Select(p => Path.GetRelativePath(root, p)).ToArray(), result.Animations, null);
                    File.Delete(marker);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    lock (sync) cancelled = true;
                    File.WriteAllText(marker, "已取消。此主资源的文件可能不完整。");
                    item = new(group.ResourcePath, "cancelled", [], 0, "用户取消");
                }
                catch (Exception ex)
                {
                    File.WriteAllText(marker, ex.ToString());
                    item = new(group.ResourcePath, "failed", [], 0, ex.Message);
                }
                lock (sync)
                {
                    results.Add(item);
                    journal.WriteLine(JsonConvert.SerializeObject(item));
                    progress?.Invoke($"已处理 {results.Count}/{plan.Groups.Length} · 并发 {maxConcurrency} · {group.ResourcePath}");
                }
            });
        }
        finally { WriteManifest(cancelled || token.IsCancellationRequested ? "cancelled" : results.Count < plan.Groups.Length ? "interrupted" : results.Any(r => r.Status == "failed") ? "partial" : "complete"); }
        return new(root, results.Count(r => r.Status == "success"), results.Count(r => r.Status == "failed"),
            plan.Groups.Length - results.Count(r => r.Status is "success" or "failed"), cancelled || token.IsCancellationRequested);
    }
}
