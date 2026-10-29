using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnimeStudio.AssetExplorer;
using Newtonsoft.Json;

namespace AnimeStudio.GUI;

internal static class AssetPreviewWorker
{
    private const string Prefix = "AEX-PREVIEW:";
    private sealed record Reply(AssetPreviewData Data = null, string Error = null);

    public static bool TryRun(string[] args)
    {
        if (args.Length != 1 || args[0] != "--asset-explorer-preview-worker") return false;
        Console.InputEncoding = Console.OutputEncoding = Encoding.UTF8;
        var protocol = Console.Out; Console.SetOut(TextWriter.Null); Logger.Silent = Progress.Silent = true;
        using var reader = new AssetPreviewReader(Console.ReadLine());
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            Reply reply;
            try { reply = new(reader.Read(JsonConvert.DeserializeObject<CatalogRequest>(line), CancellationToken.None)); }
            catch (Exception ex) { reply = new(Error: ex.GetBaseException().Message); }
            protocol.WriteLine(Prefix + JsonConvert.SerializeObject(reply)); protocol.Flush();
        }
        return true;
    }

    internal sealed class Client(string unityVersion) : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private Process process;
        private Task<string> errors;
        private volatile bool disposed;

        public async Task<AssetPreviewData> ReadAsync(CatalogRequest request, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this);
                if (process == null || process.HasExited)
                {
                    Stop();
                    var assembly = typeof(AssetPreviewWorker).Assembly.Location;
                    var start = new ProcessStartInfo(Path.ChangeExtension(assembly, ".exe"))
                    {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                        WorkingDirectory = Path.GetDirectoryName(assembly)
                    };
                    start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
                    start.ArgumentList.Add("--asset-explorer-preview-worker");
                    process = Process.Start(start) ?? throw new IOException("无法启动预览工作进程。");
                    try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (System.ComponentModel.Win32Exception) { }
                    errors = process.StandardError.ReadToEndAsync();
                    await process.StandardInput.WriteLineAsync((unityVersion ?? "").AsMemory(), token).ConfigureAwait(false);
                }
                var running = process;
                using var cancel = token.Register(() => { _ = Task.Run(() => Kill(running)); });
                await process.StandardInput.WriteLineAsync(JsonConvert.SerializeObject(request).AsMemory(), token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
                    if (line == null) throw new IOException("预览工作进程已退出：" + await errors.ConfigureAwait(false));
                    if (!line.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                    var reply = JsonConvert.DeserializeObject<Reply>(line[Prefix.Length..]);
                    if (reply.Error != null) throw new IOException(reply.Error);
                    return reply.Data ?? throw new InvalidDataException("预览工作进程返回空结果。");
                }
            }
            catch { Stop(); token.ThrowIfCancellationRequested(); throw; }
            finally { gate.Release(); }
        }

        private static void Kill(Process target)
        {
            try { if (target != null && !target.HasExited) target.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private void Stop()
        {
            Kill(process); process?.WaitForExit(3000); process?.Dispose(); process = null;
        }

        public void Dispose()
        {
            disposed = true;
            // Window closing never waits for a decompressor or native decoder to stop.
            _ = Task.Run(async () => { await gate.WaitAsync().ConfigureAwait(false); try { Stop(); } finally { gate.Release(); } });
        }
    }
}
