using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeStudio;

public static partial class AssetsHelper
{
    // Optional legacy CLI indexing controls; the GUI scanner runs independently.
    public static bool ComputeHash = true;
    public static int ReadAhead = 0;

    private sealed class Prefetcher : IDisposable
    {
        private readonly IReadOnlyList<string> _files;
        private readonly int _depth;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _worker;
        private int _cursor = -1;

        public Prefetcher(IReadOnlyList<string> files, int depth)
        {
            _files = files;
            _depth = depth;
            _worker = Task.Run(Run);
        }

        /// <summary>告诉预读线程主线程处理到哪了，避免跑得太靠前。</summary>
        public void Advance(int index) => Volatile.Write(ref _cursor, index);

        private void Run()
        {
            var buffer = new byte[1 << 20];
            var token = _cts.Token;
            for (var next = 0; next < _files.Count && !token.IsCancellationRequested; )
            {
                // 领先太多没意义：先读进来的会被后读的挤出缓存，白费一次磁盘往返
                if (next - Volatile.Read(ref _cursor) > _depth)
                {
                    Thread.Sleep(5);
                    continue;
                }
                try
                {
                    using var fs = new FileStream(_files[next], FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
                    while (fs.Read(buffer, 0, buffer.Length) > 0)
                    {
                        if (token.IsCancellationRequested) return;
                    }
                }
                catch
                {
                    // 预读只是优化，读不到就让主线程走正常路径
                }
                next++;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }

}
