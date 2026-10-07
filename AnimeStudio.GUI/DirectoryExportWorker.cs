using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AnimeStudio.AssetExplorer;
using Newtonsoft.Json;

namespace AnimeStudio.GUI;

// Each worker owns its native exporters, current directory, type flags and CAB cache.
internal static partial class DirectoryExportWorker
{
    private const string Prefix = "AEX-EXPORT:";
    private sealed record Setup(CatalogRequest Context, ModelExportService.Options Options, Dictionary<string, string> Settings);
    private sealed record Job(PrimaryAssetGroup Group, string Output);
    private sealed record Reply(string Kind, PrimaryExportResult Result = null, string Error = null, string[] Paths = null);

    public static bool TryRun(string[] args)
    {
        if (args.Length != 1 || args[0] != "--asset-explorer-export-worker") return false;
        Console.InputEncoding = Console.OutputEncoding = Encoding.UTF8;
        var protocol = Console.Out;
        Console.SetOut(TextWriter.Null);
        void Send(Reply reply) { protocol.WriteLine(Prefix + JsonConvert.SerializeObject(reply)); protocol.Flush(); }
        try
        {
            Logger.Silent = true;
            TypeFlags.SetTypes(null);
            var setup = JsonConvert.DeserializeObject<Setup>(Console.ReadLine());
            foreach (var setting in setup.Settings)
            {
                var property = Properties.Settings.Default.Properties[setting.Key];
                if (property != null) Properties.Settings.Default[setting.Key] = JsonConvert.DeserializeObject(setting.Value, property.PropertyType);
            }
            var context = setup.Context with { MeshCatalog = new RemoteMeshes(Send) };
            var map = string.IsNullOrWhiteSpace(context.CabMap) ? null : CabCatalog.Read(context.CabMap, CancellationToken.None);
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                try
                {
                    var job = JsonConvert.DeserializeObject<Job>(line);
                    Send(new("result", Export(context, job.Group, job.Output, setup.Options, map, CancellationToken.None)));
                }
                catch (Exception ex) { Send(new("error", Error: ex.ToString())); }
            }
        }
        catch (Exception ex) { Send(new("error", Error: ex.ToString())); Environment.ExitCode = 1; }
        return true;
    }

    private sealed class RemoteMeshes(Action<Reply> send) : IMeshCatalog
    {
        public IReadOnlyDictionary<string, CatalogAsset[]> FindMeshes(IEnumerable<string> paths, CancellationToken token)
        {
            send(new("meshes", Paths: paths.ToArray()));
            var line = Console.ReadLine() ?? throw new EndOfStreamException("导出控制进程已退出。");
            return new Dictionary<string, CatalogAsset[]>(JsonConvert.DeserializeObject<Dictionary<string, CatalogAsset[]>>(line), StringComparer.OrdinalIgnoreCase);
        }
    }

    public static async Task<DirectoryExportResult> ExportAsync(DirectoryExportPlan plan, CatalogRequest context,
        string output, ModelExportService.Options options, Action<string> report, CancellationToken token)
    {
        var count = Math.Min(Math.Clamp(context.ExportWorkers, 1, 8), Math.Max(1, plan.Groups.Length));
        report?.Invoke($"正在启动导出工作进程 · 并发 {count} · 共 {plan.Groups.Length} 个主资源…");
        var clients = Enumerable.Range(0, count).Select(_ => new Client(context, options)).ToArray();
        var available = Channel.CreateBounded<Client>(count);
        foreach (var client in clients) available.Writer.TryWrite(client);
        try
        {
            return await DirectoryExportService.RunAsync(plan, output, async (group, folder, cancellation) =>
            {
                var client = await available.Reader.ReadAsync(cancellation);
                try { return await client.ExportAsync(group, folder, cancellation).ConfigureAwait(false); }
                finally { available.Writer.TryWrite(client); }
            }, report, token, count);
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    private sealed class Client(CatalogRequest context, ModelExportService.Options options) : IDisposable
    {
        private Process process;
        private Task<string> errors;
        private string scratch;

        private async Task StartAsync(CancellationToken token)
        {
            if (process != null && !process.HasExited) return;
            Dispose();
            scratch = Directory.CreateTempSubdirectory("aex-worker-").FullName;
            var assembly = typeof(DirectoryExportWorker).Assembly.Location;
            var start = new ProcessStartInfo(Path.ChangeExtension(assembly, ".exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(assembly)
            };
            start.Environment["TMP"] = start.Environment["TEMP"] = scratch;
            start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
            start.ArgumentList.Add("--asset-explorer-export-worker");
            process = Process.Start(start) ?? throw new IOException("无法启动导出工作进程。");
            errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync(JsonConvert.SerializeObject(new Setup(context, options, Properties.Settings.Default.Properties.Cast<System.Configuration.SettingsProperty>()
                .ToDictionary(p => p.Name, p => JsonConvert.SerializeObject(Properties.Settings.Default[p.Name])))).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }

        public async Task<PrimaryExportResult> ExportAsync(PrimaryAssetGroup group, string output, CancellationToken token)
        {
            try
            {
                await StartAsync(token).ConfigureAwait(false);
                using var cancellation = token.Register(Kill);
                await process.StandardInput.WriteLineAsync(JsonConvert.SerializeObject(new Job(group, output)).AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
                    if (line == null) throw new IOException("导出工作进程意外退出：" + await errors);
                    var index = line.IndexOf(Prefix, StringComparison.Ordinal);
                    if (index < 0) continue;
                    var reply = JsonConvert.DeserializeObject<Reply>(line[(index + Prefix.Length)..]);
                    if (reply.Kind == "error") throw new IOException(reply.Error);
                    if (reply.Kind == "result") return reply.Result ?? throw new IOException("工作进程返回空结果。");
                    if (reply.Kind == "meshes")
                    {
                        // Query the parent's existing index; don't duplicate the multi-million-row catalog per worker.
                        var meshes = await Task.Run(() => context.MeshCatalog?.FindMeshes(reply.Paths, token)
                            ?? new Dictionary<string, CatalogAsset[]>(), token).ConfigureAwait(false);
                        await process.StandardInput.WriteLineAsync(JsonConvert.SerializeObject(meshes).AsMemory(), token);
                        await process.StandardInput.FlushAsync(token);
                    }
                }
            }
            catch
            {
                Dispose();
                token.ThrowIfCancellationRequested();
                throw;
            }
        }

        private void Kill()
        {
            try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            Kill();
            if (process != null) { process.WaitForExit(); process.Dispose(); process = null; }
            if (scratch != null)
            {
                try { Directory.Delete(scratch, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                scratch = null;
            }
        }
    }
}
