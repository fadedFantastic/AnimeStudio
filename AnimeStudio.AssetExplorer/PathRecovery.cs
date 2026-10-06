using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using K4os.Hash.xxHash;

namespace AnimeStudio.AssetExplorer
{
    /// <summary>
    /// 把清单里没还原出来的 container 补齐。
    ///
    /// ZZZ 打包时把资源路径换成了 XXH64(路径小写, seed 0)，外部字典（Z3-Asset-Map）
    /// 只是别人爆破出来的一份 hash → 路径对照表，新版本新增的资源它必然缺。
    /// 缺了的那些 Container 字段就停在十进制 hash 上，界面里看到的就是一串数字。
    ///
    /// 但 hash 自己也能算：猜对路径 hash 一定对得上，对不上就是猜错。
    /// 所以这里不存在「猜得像」——命中即确证，不会写进错误路径。
    /// 于是问题变成怎么把候选路径凑出来，靠三样东西：
    ///   1. 资源名多半就是文件名（MAT_Xxx → MAT_Xxx.mat）；
    ///   2. 同一个 blk 里已经解析出来的路径，指出了目录大概在哪；
    ///   3. 资源名里的词（PasSeul、Ramiel…）常常就是路径里新建的那一层目录名。
    /// </summary>
    internal static class PathRecovery
    {
        /// <summary>按类型缩小扩展名候选，省掉大量无谓的 hash。</summary>
        private static readonly Dictionary<string, string[]> ExtByType =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["Material"] = new[] { ".mat" },
                ["Texture2D"] = new[] { ".png", ".tga", ".psd", ".jpg", ".exr", ".tif", ".tiff", ".dds", ".hdr" },
                ["Texture"] = new[] { ".png", ".tga", ".psd", ".jpg", ".exr" },
                ["Sprite"] = new[] { ".png", ".tga", ".psd", ".jpg" },
                ["SpriteAtlas"] = new[] { ".spriteatlas" },
                ["Mesh"] = new[] { ".fbx", ".obj", ".asset", ".mesh" },
                ["MeshFilter"] = new[] { ".fbx", ".prefab" },
                ["MeshRenderer"] = new[] { ".fbx", ".prefab" },
                ["SkinnedMeshRenderer"] = new[] { ".fbx", ".prefab" },
                ["AnimationClip"] = new[] { ".anim", ".fbx", ".controller" },
                ["Animation"] = new[] { ".anim", ".fbx" },
                ["Animator"] = new[] { ".prefab", ".controller" },
                ["AnimatorController"] = new[] { ".controller" },
                ["AnimatorOverrideController"] = new[] { ".overridecontroller" },
                ["Avatar"] = new[] { ".fbx", ".asset" },
                ["Shader"] = new[] { ".shader", ".shadergraph", ".compute" },
                ["TextAsset"] = new[] { ".txt", ".bytes", ".json", ".xml", ".csv", ".asset", ".shader", ".cs" },
                ["AudioClip"] = new[] { ".wav", ".mp3", ".ogg", ".aiff", ".aif" },
                ["VideoClip"] = new[] { ".mp4", ".webm", ".mov", ".avi" },
                ["MovieTexture"] = new[] { ".mp4", ".webm", ".mov" },
                ["Font"] = new[] { ".ttf", ".otf", ".fontsettings", ".asset" },
                ["GameObject"] = new[] { ".prefab", ".fbx" },
                ["Transform"] = new[] { ".prefab", ".fbx" },
                ["RectTransform"] = new[] { ".prefab" },
                ["MonoBehaviour"] = new[] { ".asset", ".prefab", ".playable", ".mat", ".controller" },
                ["MonoScript"] = new[] { ".cs", ".dll" },
                ["AssetBundle"] = new[] { ".prefab", ".asset" },
            };

        /// <summary>类型没命中上表时用的通配集合。</summary>
        private static readonly string[] ExtFallback =
        {
            ".prefab", ".asset", ".mat", ".png", ".anim", ".controller", ".fbx",
            ".txt", ".bytes", ".shader", ".tga", ".json", ""
        };

        // 出现在超过这么多目录里的词（Assets、Char、ART…）没有区分度，索引时丢掉
        private const int TokenDirLimit = 1500;
        // 一个 target 最多试多少个相关目录
        private const int MaxCandidateDirs = 384;
        // 派生新目录时，从最相关的几个已知目录出发
        private const int DeriveSeedDirs = 48;
        // 资源名最多切出多少个词
        private const int MaxTokens = 24;
        // 推测目录时：出现在这么多目录里的词（Prop、Models、Char…）满大街都是，
        // 对上了也说明不了什么，不算分。只有稀有词对上才算真沾亲带故。
        private const int GuessCommonTokenLimit = 200;

        public sealed class Request
        {
            public string[] Name;
            public string[] Type;
            public string[] Container;   // 就地改写：解出来的直接写回去
            public string[] Blk;
            public int Count;

            /// <summary>外部字典，用来提供已知目录。</summary>
            public IReadOnlyDictionary<ulong, string> KnownPaths;

            /// <summary>
            /// 前几轮只在「同 blk 目录」和「资源名相关目录」里找，很快。
            /// 打开这个会再拿全部已知目录跟剩下的硬碰一遍，慢得多。
            /// </summary>
            public bool DeepScan;
        }

        public sealed class Result
        {
            public Dictionary<ulong, string> Recovered = new Dictionary<ulong, string>();
            public int TargetHashes;     // 待解的 hash 去重后有多少个
            public int NamelessHashes;   // 其中名字没有信息量、根本无从猜起的
            public int SolvedHashes;     // 解出来几个
            public int PatchedRows;      // 清单里有多少行因此被改写
        }

        // ------------------------------------------------------------------ hash

        public static ulong HashOf(string path)
        {
            var bytes = Encoding.UTF8.GetBytes(path.ToLowerInvariant());
            return XXH64.DigestOf(bytes, 0, bytes.Length);
        }

        private static byte[] Lower(string s) => Encoding.UTF8.GetBytes(s.ToLowerInvariant());

        [ThreadStatic] private static byte[] _buf;

        /// <summary>拼 目录+文件名+扩展名 直接算 hash，全程不产生 string。</summary>
        private static ulong HashParts(byte[] a, byte[] b, byte[] c)
        {
            var n = a.Length + b.Length + c.Length;
            var buf = _buf;
            if (buf == null || buf.Length < n) _buf = buf = new byte[Math.Max(1024, n * 2)];
            Buffer.BlockCopy(a, 0, buf, 0, a.Length);
            Buffer.BlockCopy(b, 0, buf, a.Length, b.Length);
            Buffer.BlockCopy(c, 0, buf, a.Length + b.Length, c.Length);
            return XXH64.DigestOf(buf, 0, n);
        }

        // ------------------------------------------------------------------ 模型

        /// <summary>
        /// 一个待解的 container hash。同一个 container 下往往挂着好几个对象
        /// （prefab 的 GameObject、里面的 Material、Mesh…），它们的名字都是候选文件名。
        /// </summary>
        private sealed class Target
        {
            public ulong Hash;
            public readonly List<string> Names = new List<string>(2);
            public readonly List<string> Types = new List<string>(2);
            public string Solved;

            // 小写字节形式，整个流程里反复用，只算一次
            private byte[][] _lowNames;
            private byte[][][] _lowExts;
            private List<string> _tokens;

            public void AddMember(string name, string type)
            {
                if (Names.Count >= 8 || IsUselessName(name, type)) return;
                AddName(name, type);

                // Shader 的名字是它的注册名（miHoYo/Scene/BlitAdd），
                // 文件名一般只是最后一段
                var cut = name.LastIndexOf('/');
                if (cut > 0 && cut < name.Length - 1) AddName(name.Substring(cut + 1), type);
            }

            /// <summary>
            /// fbx 导入进来的网格/材质/动画，名字是子资产名而不是文件名。
            /// 但同一个 fbx 里那批子资产的公共前缀，往往就是 fbx 自己的名字：
            ///   Bangboo_Xxx_Ani_Run01 / _Run02_End / _Run02_Loop  →  Bangboo_Xxx_Ani_Run
            /// 再顺手削掉 _Lod1 / _01 这类尾巴。都只是候选，猜错了 hash 自然对不上。
            /// 必须等成员都收完了再调。
            /// </summary>
            public void EnrichNames()
            {
                var seed = Names.Count;
                if (seed == 0) return;
                var type = Types[0];

                if (seed > 1)
                {
                    var prefix = Names[0];
                    for (var i = 1; i < seed && prefix.Length > 0; i++)
                    {
                        var n = Math.Min(prefix.Length, Names[i].Length);
                        var k = 0;
                        while (k < n && prefix[k] == Names[i][k]) k++;
                        prefix = prefix.Substring(0, k);
                    }
                    AddName(TrimTail(prefix), type);
                }

                // 每个名字削一到两截尾巴
                for (var i = 0; i < seed; i++)
                {
                    var s = TrimTail(Names[i]);
                    AddName(s, type);
                    AddName(TrimTail(s), type);
                }
            }

            /// <summary>削掉最后一个 _ 之后的部分，Lod1 / 01 / End 这些。</summary>
            private static string TrimTail(string s)
            {
                if (string.IsNullOrEmpty(s)) return s;
                var cut = s.TrimEnd('_').LastIndexOf('_');
                return cut >= 3 ? s.Substring(0, cut) : string.Empty;
            }

            private void AddName(string name, string type)
            {
                if (Names.Count >= 14 || string.IsNullOrEmpty(name) || name.Length < 3) return;
                for (var i = 0; i < Names.Count; i++)
                    if (string.Equals(Names[i], name, StringComparison.Ordinal) &&
                        string.Equals(Types[i], type, StringComparison.Ordinal)) return;
                Names.Add(name);
                Types.Add(type);
            }

            /// <summary>
            /// 名字里没有信息就别浪费时间了：
            /// 匿名 MonoBehaviour 的名字就是字面的 "MonoBehaviour"（清单里一百多万条），
            /// 还有一批资源直接拿 hash 当名字。这些拿去跟目录硬碰纯属空转。
            /// </summary>
            public static bool IsUselessName(string name, string type)
            {
                if (string.IsNullOrEmpty(name) || name.Length < 3) return true;
                if (type != null && string.Equals(name, type, StringComparison.Ordinal)) return true;
                if (IsDigits(name)) return true;
                if (name.StartsWith("BinFile #", StringComparison.Ordinal)) return true;
                return false;
            }

            /// <summary>没有一个能用的名字，这个 hash 就没法猜。</summary>
            public bool Hopeless => Names.Count == 0;

            public void Prepare()
            {
                if (_lowNames != null) return;
                var n = Names.Count;
                _lowNames = new byte[n][];
                _lowExts = new byte[n][][];
                for (var i = 0; i < n; i++)
                {
                    _lowNames[i] = Lower(Names[i]);
                    var exts = ExtensionsFor(i < Types.Count ? Types[i] : null);
                    var arr = new byte[exts.Length][];
                    for (var k = 0; k < exts.Length; k++) arr[k] = Lower(exts[k]);
                    _lowExts[i] = arr;
                }
            }

            public byte[][] LowNames { get { Prepare(); return _lowNames; } }
            public byte[][][] LowExts { get { Prepare(); return _lowExts; } }

            /// <summary>资源名里的词，既用来找相关目录，也用来派生新目录。</summary>
            public List<string> Tokens
            {
                get
                {
                    if (_tokens != null) return _tokens;
                    _tokens = new List<string>(8);
                    foreach (var name in Names) SplitTokens(name, _tokens);
                    return _tokens;
                }
            }
        }

        /// <summary>目录候选池。目录字符串只存一份，外面一律用下标引用。</summary>
        private sealed class DirPool
        {
            private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly List<string> Dirs = new List<string>();
            public readonly List<byte[]> Lowered = new List<byte[]>();

            public int Count => Dirs.Count;

            public bool Add(string dir)
            {
                if (string.IsNullOrEmpty(dir) || _index.ContainsKey(dir)) return false;
                _index[dir] = Dirs.Count;
                Dirs.Add(dir);
                Lowered.Add(Lower(dir));
                return true;
            }
        }

        // ------------------------------------------------------------------ 主流程

        public static Result Recover(Request req, Action<string> onProgress, CancellationToken token)
        {
            var result = new Result();
            if (req == null || req.Count <= 0) return result;

            // ---- 1. 收集待解 hash，顺带记下每个 blk 里已经解析出来的目录
            onProgress?.Invoke("路径补全：收集未解析条目…");

            var targets = new Dictionary<ulong, Target>();
            var blkDirs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var blkTargets = new Dictionary<string, List<Target>>(StringComparer.OrdinalIgnoreCase);
            var blkSeen = new Dictionary<string, HashSet<ulong>>(StringComparer.OrdinalIgnoreCase);
            var pool = new DirPool();

            for (var i = 0; i < req.Count; i++)
            {
                if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();

                var container = req.Container[i];
                if (string.IsNullOrEmpty(container)) continue;   // 压根没 container，无从下手
                var blk = req.Blk != null ? req.Blk[i] : string.Empty;

                if (IsDigits(container))
                {
                    if (!ulong.TryParse(container, out var hash)) continue;

                    if (!targets.TryGetValue(hash, out var t))
                        targets[hash] = t = new Target { Hash = hash };
                    t.AddMember(req.Name[i], req.Type != null ? req.Type[i] : null);

                    if (!blkSeen.TryGetValue(blk, out var seen))
                        blkSeen[blk] = seen = new HashSet<ulong>();
                    if (seen.Add(hash))
                    {
                        if (!blkTargets.TryGetValue(blk, out var list))
                            blkTargets[blk] = list = new List<Target>();
                        list.Add(t);
                    }
                }
                else
                {
                    // 已经解析出来的路径 —— 它的目录是最靠谱的候选
                    var dir = DirOf(container);
                    if (dir == null) continue;
                    pool.Add(dir);
                    if (!blkDirs.TryGetValue(blk, out var set))
                        blkDirs[blk] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(dir);
                }
            }
            blkSeen = null;

            // 成员收齐了，再从这批名字里派生出更多候选文件名
            foreach (var t in targets.Values) t.EnrichNames();

            result.TargetHashes = targets.Count;
            foreach (var t in targets.Values)
                if (t.Hopeless) result.NamelessHashes++;

            var guessable = result.TargetHashes - result.NamelessHashes;
            if (guessable == 0)
            {
                onProgress?.Invoke($"路径补全：{result.TargetHashes:N0} 个未知 hash 的名字都没有信息量，无从补起");
                return result;
            }

            // ---- 2. 外部字典里的目录也收进来
            if (req.KnownPaths != null)
            {
                onProgress?.Invoke("路径补全：整理已知目录…");
                foreach (var p in req.KnownPaths.Values)
                {
                    var dir = DirOf(p);
                    if (dir != null) pool.Add(dir);
                }
            }
            token.ThrowIfCancellationRequested();
            onProgress?.Invoke(
                $"路径补全：{guessable:N0} 个 hash 可猜（另有 {result.NamelessHashes:N0} 个名字没信息量），" +
                $"{pool.Count:N0} 个已知目录");

            // ---- 3. 第一轮：只在同 blk 已解析出来的目录里找。命中率最高，也最便宜
            var solved = SolveByBlk(blkTargets, blkDirs, pool, onProgress, token);
            var pending = Remaining(targets.Values);
            onProgress?.Invoke($"路径补全：同 blk 目录解出 {solved:N0}，剩 {pending.Count:N0}");

            blkTargets = null;
            blkDirs = null;

            // ---- 4. 后续几轮：按资源名里的词找相关目录，并试着派生新目录。
            //         每解出一条就多一个已知目录，下一轮又能带出更多，所以要迭代。
            for (var round = 0; round < 3 && pending.Count > 0; round++)
            {
                token.ThrowIfCancellationRequested();

                var index = BuildTokenIndex(pool, token);
                var before = pending.Count;

                solved += SolveByTokens(pending, pool, index, onProgress, token);
                pending = Remaining(pending);

                onProgress?.Invoke($"路径补全：第 {round + 2} 轮后剩 {pending.Count:N0}");
                if (pending.Count == before) break;   // 这轮没进展，再来一轮也一样
            }

            // ---- 5. 兜底：拿全部已知目录跟剩下的硬碰。很慢，默认不开
            if (req.DeepScan && pending.Count > 0)
            {
                solved += SolveExhaustive(pending, pool, onProgress, token);
                pending = Remaining(pending);
            }

            // ---- 6. 回填
            foreach (var t in targets.Values)
                if (t.Solved != null) result.Recovered[t.Hash] = t.Solved;

            result.SolvedHashes = solved;
            result.PatchedRows = Apply(req, result.Recovered, token);
            onProgress?.Invoke(
                $"路径补全：可猜的 {guessable:N0} 个 hash 里解出 {solved:N0} 个，改写 {result.PatchedRows:N0} 条");
            return result;
        }

        private static List<Target> Remaining(IEnumerable<Target> list)
        {
            var next = new List<Target>();
            foreach (var t in list)
                if (t.Solved == null && !t.Hopeless) next.Add(t);
            return next;
        }

        // ------------------------------------------------------------------ 第一轮：同 blk

        private static int SolveByBlk(
            Dictionary<string, List<Target>> blkTargets,
            Dictionary<string, HashSet<string>> blkDirs,
            DirPool pool,
            Action<string> onProgress,
            CancellationToken token)
        {
            var solved = 0;
            var done = 0;
            var newDirs = new List<string>();
            var sync = new object();
            var keys = new List<string>(blkTargets.Keys);

            Parallel.ForEach(keys, key =>
            {
                if (token.IsCancellationRequested) return;

                var n = Interlocked.Increment(ref done);
                if ((n & 0x1FF) == 0)
                    onProgress?.Invoke($"路径补全：同 blk 匹配 {n:N0}/{keys.Count:N0}");

                if (!blkDirs.TryGetValue(key, out var dirs) || dirs.Count == 0) return;

                var raw = new List<string>(dirs);
                var low = new List<byte[]>(raw.Count);
                foreach (var d in raw) low.Add(Lower(d));

                var localSolved = 0;
                var localDirs = new List<string>();

                foreach (var t in blkTargets[key])
                {
                    if (t.Solved != null || t.Hopeless) continue;

                    if (TrySolve(t, raw, low, out var hit))
                    {
                        t.Solved = hit;
                        localSolved++;
                        localDirs.Add(DirOf(hit));
                        continue;
                    }

                    // 同 blk 的目录里没有，就用资源名里的词从这些目录派生一批再试
                    var derived = new List<string>();
                    foreach (var d in raw) Derive(d, t, derived);
                    if (derived.Count == 0) continue;

                    var dl = new List<byte[]>(derived.Count);
                    foreach (var d in derived) dl.Add(Lower(d));
                    if (TrySolve(t, derived, dl, out hit))
                    {
                        t.Solved = hit;
                        localSolved++;
                        localDirs.Add(DirOf(hit));
                    }
                }

                if (localSolved == 0) return;
                lock (sync)
                {
                    solved += localSolved;
                    newDirs.AddRange(localDirs);
                }
            });

            token.ThrowIfCancellationRequested();
            foreach (var d in newDirs) pool.Add(d);
            return solved;
        }

        // ------------------------------------------------------------------ 后续轮：按资源名找目录

        /// <summary>词 → 含这个词的目录下标。目录几万个，逐个碰太慢，靠这个把范围压到几十个。</summary>
        private static Dictionary<string, List<int>> BuildTokenIndex(DirPool pool, CancellationToken token)
        {
            // 先数一遍：太常见的词留着只会拖慢，不建索引
            var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var buf = new List<string>(16);
            for (var i = 0; i < pool.Count; i++)
            {
                if ((i & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
                buf.Clear();
                SplitTokens(pool.Dirs[i], buf);
                foreach (var tok in buf)
                {
                    freq.TryGetValue(tok, out var c);
                    freq[tok] = c + 1;
                }
            }

            var index = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < pool.Count; i++)
            {
                if ((i & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
                buf.Clear();
                SplitTokens(pool.Dirs[i], buf);
                foreach (var tok in buf)
                {
                    if (tok.Length < 3 || freq[tok] > TokenDirLimit) continue;
                    if (!index.TryGetValue(tok, out var list))
                        index[tok] = list = new List<int>();
                    list.Add(i);
                }
            }
            return index;
        }

        private static int SolveByTokens(
            List<Target> pending,
            DirPool pool,
            Dictionary<string, List<int>> index,
            Action<string> onProgress,
            CancellationToken token)
        {
            var solved = 0;
            var done = 0;
            var newDirs = new List<string>();
            var sync = new object();

            Parallel.ForEach(
                Partitioner(pending.Count),
                () => new Local(),
                (range, state, local) =>
                {
                    for (var i = range.Item1; i < range.Item2; i++)
                    {
                        if ((i & 0xFF) == 0)
                        {
                            if (token.IsCancellationRequested) { state.Stop(); break; }
                            var n = Interlocked.Add(ref done, 0x100);
                            onProgress?.Invoke($"路径补全：按资源名匹配 {Math.Min(n, pending.Count):N0}/{pending.Count:N0}");
                        }

                        var t = pending[i];
                        if (t.Solved != null) continue;

                        // 资源名里的词 → 相关目录，重合的词越多越可能是它
                        local.Hits.Clear();
                        foreach (var tok in t.Tokens)
                        {
                            if (tok.Length < 3 || !index.TryGetValue(tok, out var list)) continue;
                            foreach (var d in list)
                            {
                                local.Hits.TryGetValue(d, out var c);
                                local.Hits[d] = c + 1;
                            }
                        }
                        if (local.Hits.Count == 0) continue;

                        // 按重合词数分桶取前几百个。这里一条资源可能牵出上万个候选目录，
                        // 真去排序的话几十万条乘下来能跑到小时级，分桶是 O(n) 的。
                        local.ClearBuckets();
                        foreach (var kv in local.Hits)
                            local.Buckets[Math.Min(kv.Value, MaxTokens)].Add(kv.Key);

                        local.Raw.Clear();
                        local.Low.Clear();
                        for (var c = MaxTokens; c >= 1 && local.Raw.Count < MaxCandidateDirs; c--)
                        {
                            var bucket = local.Buckets[c];
                            for (var k = 0; k < bucket.Count && local.Raw.Count < MaxCandidateDirs; k++)
                            {
                                local.Raw.Add(pool.Dirs[bucket[k]]);
                                local.Low.Add(pool.Lowered[bucket[k]]);
                            }
                        }

                        if (TrySolve(t, local.Raw, local.Low, out var hit))
                        {
                            t.Solved = hit;
                            local.Solved++;
                            local.NewDirs.Add(DirOf(hit));
                            continue;
                        }

                        // 相关目录里也没有，就从最相关的那几个往下/往旁边派生新目录再试
                        local.Derived.Clear();
                        var seed = Math.Min(local.Raw.Count, DeriveSeedDirs);
                        for (var k = 0; k < seed; k++) Derive(local.Raw[k], t, local.Derived);
                        if (local.Derived.Count == 0) continue;

                        local.DerivedLow.Clear();
                        foreach (var d in local.Derived) local.DerivedLow.Add(Lower(d));
                        if (TrySolve(t, local.Derived, local.DerivedLow, out hit))
                        {
                            t.Solved = hit;
                            local.Solved++;
                            local.NewDirs.Add(DirOf(hit));
                        }
                    }
                    return local;
                },
                local =>
                {
                    lock (sync)
                    {
                        solved += local.Solved;
                        newDirs.AddRange(local.NewDirs);
                    }
                });

            token.ThrowIfCancellationRequested();
            foreach (var d in newDirs) pool.Add(d);
            return solved;
        }

        /// <summary>每个工作线程复用的临时容器，免得几十万次循环全在造垃圾。</summary>
        private sealed class Local
        {
            public readonly Dictionary<int, int> Hits = new Dictionary<int, int>();
            public readonly List<int>[] Buckets = NewBuckets();
            public readonly List<string> Raw = new List<string>();
            public readonly List<byte[]> Low = new List<byte[]>();
            public readonly List<string> Derived = new List<string>();
            public readonly List<byte[]> DerivedLow = new List<byte[]>();
            public readonly List<string> NewDirs = new List<string>();
            public int Solved;

            private static List<int>[] NewBuckets()
            {
                var b = new List<int>[MaxTokens + 1];
                for (var i = 0; i < b.Length; i++) b[i] = new List<int>();
                return b;
            }

            public void ClearBuckets()
            {
                for (var i = 0; i < Buckets.Length; i++) Buckets[i].Clear();
            }
        }

        private static IEnumerable<Tuple<int, int>> Partitioner(int count)
        {
            var workers = Math.Max(1, Environment.ProcessorCount - 1);
            var chunk = Math.Max(256, (count + workers - 1) / workers);
            for (var i = 0; i < count; i += chunk)
                yield return Tuple.Create(i, Math.Min(i + chunk, count));
        }

        // ------------------------------------------------------------------ 兜底：全目录

        private static int SolveExhaustive(
            List<Target> pending, DirPool pool, Action<string> onProgress, CancellationToken token)
        {
            onProgress?.Invoke($"路径补全：深度扫描 {pending.Count:N0} 条 × {pool.Count:N0} 目录…");

            var solved = 0;
            var done = 0;
            var sync = new object();

            Parallel.ForEach(pending, () => 0, (t, state, local) =>
            {
                if (token.IsCancellationRequested) { state.Stop(); return local; }

                var n = Interlocked.Increment(ref done);
                if ((n & 0x3FF) == 0)
                    onProgress?.Invoke($"路径补全：深度扫描 {n:N0}/{pending.Count:N0}");

                if (t.Solved != null) return local;
                if (!TrySolve(t, pool.Dirs, pool.Lowered, out var hit)) return local;
                t.Solved = hit;
                return local + 1;
            }, local => { lock (sync) solved += local; });

            token.ThrowIfCancellationRequested();
            return solved;
        }

        // ------------------------------------------------------------------ 单个 target 的尝试

        /// <summary>目录 × 名字 × 扩展名 全试一遍，hash 相等就是它。</summary>
        private static bool TrySolve(
            Target t, IReadOnlyList<string> dirsRaw, IReadOnlyList<byte[]> dirsLow, out string hit)
        {
            hit = null;
            var names = t.LowNames;
            var exts = t.LowExts;

            for (var d = 0; d < dirsLow.Count; d++)
            {
                var dir = dirsLow[d];
                for (var i = 0; i < names.Length; i++)
                {
                    var el = exts[i];
                    for (var k = 0; k < el.Length; k++)
                    {
                        if (HashParts(dir, names[i], el[k]) != t.Hash) continue;
                        hit = dirsRaw[d] + t.Names[i] + Encoding.UTF8.GetString(el[k]);
                        return true;
                    }
                }
            }
            return false;
        }

        private static string[] ExtensionsFor(string type)
        {
            if (type != null && ExtByType.TryGetValue(type, out var list)) return list;
            return ExtFallback;
        }

        // ------------------------------------------------------------------ 目录派生

        /// <summary>
        /// 用资源名里的词从已知目录派生新目录：
        ///   .../Materials/Ramiel/  +  MAT_Remielle_PasSeul_Body_1
        ///     → .../Materials/PasSeul/         （末段换掉，新套装是兄弟目录）
        ///     → .../Materials/Ramiel/PasSeul/  （多一层）
        ///     → .../Materials/                 （少一层）
        /// 猜错没有代价，hash 对不上而已。
        /// </summary>
        private static void Derive(string dir, Target t, List<string> into)
        {
            if (string.IsNullOrEmpty(dir) || into.Count > 4096) return;

            var trimmed = dir.TrimEnd('/');
            var cut = trimmed.LastIndexOf('/');
            var parent = cut > 0 ? trimmed.Substring(0, cut + 1) : null;

            foreach (var tok in t.Tokens)
            {
                if (tok.Length < 3) continue;
                if (parent != null) into.Add(parent + tok + "/");
                into.Add(dir + tok + "/");
            }
            if (parent != null) into.Add(parent);
        }

        // ------------------------------------------------------------------ 目录推测

        /// <summary>
        /// 文件名猜不出来的那些，至少把目录标出来。
        ///
        /// 跟上面的还原是两回事：那边猜完能用 hash 验，对上就是铁的；
        /// 这边没有文件名就算不出 hash，验不了，只能是推测。所以结果单独存一列，
        /// 既不写回 Container，也不进补全字典 —— 免得推测混进确证里再也分不开。
        ///
        /// 依据是同一个 blk 里已经解析出来的那些路径：打包时一个 blk 基本对应一组资源，
        /// 目录八九不离十。优先挑跟资源名对得上词的那个目录，都对不上就退回公共上层目录。
        /// 只吃清单自己的数据，不需要外部字典，载入完随手就能跑。
        /// </summary>
        /// <param name="guessed">跟清单等长，推测结果写这里；没推出来的保持 null</param>
        /// <param name="confidence">可选，跟清单等长：2=资源名对上了目录里的稀有词，3=同 blk 只有一个已解析目录，1=同 blk 多个目录的公共上层</param>
        public static int GuessDirectories(
            Request req, string[] guessed, Action<string> onProgress, CancellationToken token,
            byte[] confidence = null)
        {
            if (req == null || req.Count <= 0 || guessed == null) return 0;
            onProgress?.Invoke("目录推测：整理各 blk 已解析出来的目录…");

            // ---- 1. 每个 blk 里已解析路径的目录，附带出现次数
            var blkDirs = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            // ---- 同一个 container 下的对象共用一条路径，按 hash 算一次就够
            var byHash = new Dictionary<ulong, List<string>>();
            var rowHash = new ulong[req.Count];

            for (var i = 0; i < req.Count; i++)
            {
                if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();

                var c = req.Container[i];
                if (string.IsNullOrEmpty(c)) continue;
                var blk = req.Blk != null ? req.Blk[i] : string.Empty;

                if (IsDigits(c))
                {
                    if (!ulong.TryParse(c, out var hash)) continue;

                    // 用户只要名字有意义的类型：匿名 MonoBehaviour 这类跳过
                    var name = req.Name[i];
                    var type = req.Type != null ? req.Type[i] : null;
                    if (Target.IsUselessName(name, type)) continue;

                    rowHash[i] = hash;
                    if (!byHash.TryGetValue(hash, out var names))
                        byHash[hash] = names = new List<string>(2);
                    if (names.Count < 4 && !names.Contains(name)) names.Add(name);
                }
                else
                {
                    var dir = DirOf(c);
                    if (dir == null) continue;
                    if (!blkDirs.TryGetValue(blk, out var dirs))
                        blkDirs[blk] = dirs = new Dictionary<string, int>(StringComparer.Ordinal);
                    dirs.TryGetValue(dir, out var n);
                    dirs[dir] = n + 1;
                }
            }

            if (byHash.Count == 0) return 0;
            onProgress?.Invoke($"目录推测：{byHash.Count:N0} 个 hash 待推测…");

            // ---- 2. 每个 blk 的公共上层目录（至少两个目录才谈得上「公共」），算一次存着
            var blkCommon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in blkDirs)
            {
                if (kv.Value.Count < 2) continue;
                var common = CommonPrefix(kv.Value.Keys);
                if (common != null) blkCommon[kv.Key] = common;
            }

            // ---- 3. 每个词出现在多少个不同目录里，用来分辨哪些词有区分度
            var tokenFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            {
                var allDirs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kv in blkDirs)
                    foreach (var d in kv.Value.Keys) allDirs.Add(d);

                var buf = new List<string>(16);
                foreach (var d in allDirs)
                {
                    buf.Clear();
                    SplitTokens(d, buf);
                    foreach (var t in buf)
                    {
                        tokenFreq.TryGetValue(t, out var c);
                        tokenFreq[t] = c + 1;
                    }
                }
            }

            // ---- 3. 逐条推测（同 hash 只算一次，结果缓存下来）
            var cache = new Dictionary<ulong, string>();
            var cacheConf = new Dictionary<ulong, byte>();
            var guessedRows = 0;
            var toks = new List<string>(8);

            for (var i = 0; i < req.Count; i++)
            {
                if ((i & 0xFFFF) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    onProgress?.Invoke($"目录推测：{i:N0}/{req.Count:N0}");
                }

                var hash = rowHash[i];
                if (hash == 0) continue;

                var blk = req.Blk != null ? req.Blk[i] : string.Empty;

                if (!cache.TryGetValue(hash, out var dir))
                {
                    dir = GuessOne(byHash[hash], blk, blkDirs, blkCommon, tokenFreq, toks, out var conf);
                    cache[hash] = dir;
                    cacheConf[hash] = conf;
                }
                if (dir == null) continue;

                guessed[i] = dir;
                if (confidence != null) confidence[i] = cacheConf[hash];
                guessedRows++;
            }

            onProgress?.Invoke($"目录推测：给 {guessedRows:N0} 条标出了大致目录");
            return guessedRows;
        }

        private static string GuessOne(
            List<string> names,
            string blk,
            Dictionary<string, Dictionary<string, int>> blkDirs,
            Dictionary<string, string> blkCommon,
            Dictionary<string, int> tokenFreq,
            List<string> toks,
            out byte confidence)
        {
            confidence = 0;
            if (!blkDirs.TryGetValue(blk, out var dirs) || dirs.Count == 0) return null;

            toks.Clear();
            foreach (var n in names) SplitTokens(n, toks);

            // 跟资源名对得上稀有词最多的目录；同分的挑该 blk 里出现次数多的
            string best = null;
            var bestScore = 0;
            var bestCount = 0;
            var dirToks = new List<string>(12);

            foreach (var kv in dirs)
            {
                dirToks.Clear();
                SplitTokens(kv.Key, dirToks);

                var score = 0;
                foreach (var t in toks)
                {
                    if (t.Length < 3) continue;
                    // Prop / Models 这种到处都有的词对上了不算数，
                    // 不然 Whitfield 的材质会被安到 ForbiddenArea 底下
                    if (tokenFreq.TryGetValue(t, out var freq) && freq > GuessCommonTokenLimit) continue;

                    for (var k = 0; k < dirToks.Count; k++)
                        if (string.Equals(dirToks[k], t, StringComparison.OrdinalIgnoreCase)) { score++; break; }
                }

                if (score > bestScore || (score == bestScore && score > 0 && kv.Value > bestCount))
                {
                    best = kv.Key;
                    bestScore = score;
                    bestCount = kv.Value;
                }
            }

            if (bestScore > 0)
            {
                confidence = 2;
                return best;
            }

            // 一个稀有词都对不上，退回同 blk 已解析目录的公共上层
            if (dirs.Count == 1)
            {
                foreach (var only in dirs.Keys)
                {
                    confidence = 3;
                    return only;
                }
            }
            if (blkCommon.TryGetValue(blk, out var common))
            {
                confidence = 1;
                return common;
            }
            return null;
        }

        /// <summary>一组目录的公共上层目录。浅到只剩 Assets/ 这种就没意义了，不如不给。</summary>
        private static string CommonPrefix(IEnumerable<string> dirs)
        {
            string[] common = null;
            var n = 0;

            foreach (var d in dirs)
            {
                var segs = d.TrimEnd('/').Split('/');
                if (common == null) { common = segs; n = segs.Length; continue; }

                var k = 0;
                var max = Math.Min(n, segs.Length);
                while (k < max && string.Equals(common[k], segs[k], StringComparison.Ordinal)) k++;
                n = k;
                if (n <= 2) return null;   // Assets/Xxx/ 以内，说不出什么信息
            }

            if (common == null || n <= 2) return null;
            return string.Join("/", common, 0, n) + "/";
        }

        /// <summary>
        /// 按 / \ _ - . 空格 切词。刻意不切驼峰：PasSeul 整个就是目录名，
        /// 切成 Pas + Seul 反而派生不出正确的目录。
        /// </summary>
        private static void SplitTokens(string s, List<string> into)
        {
            if (string.IsNullOrEmpty(s)) return;

            var start = -1;
            for (var i = 0; i <= s.Length; i++)
            {
                var end = i == s.Length;
                var c = end ? '/' : s[i];
                if (c == '/' || c == '\\' || c == '_' || c == '-' || c == '.' || c == ' ')
                {
                    if (start >= 0 && i - start >= 2) Add(s.Substring(start, i - start));
                    start = -1;
                }
                else if (start < 0) start = i;
            }

            void Add(string tok)
            {
                if (into.Count >= MaxTokens) return;
                for (var k = 0; k < into.Count; k++)
                    if (string.Equals(into[k], tok, StringComparison.OrdinalIgnoreCase)) return;
                into.Add(tok);
            }
        }

        private static string DirOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var i = path.LastIndexOf('/');
            return i > 0 ? path.Substring(0, i + 1) : null;
        }

        private static bool IsDigits(string s)
        {
            if (s.Length == 0 || s.Length > 20) return false;
            for (var i = 0; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        /// <summary>把解出来的路径写回 Container 列。</summary>
        private static int Apply(Request req, Dictionary<ulong, string> recovered, CancellationToken token)
        {
            if (recovered.Count == 0) return 0;
            var patched = 0;
            for (var i = 0; i < req.Count; i++)
            {
                if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
                var c = req.Container[i];
                if (string.IsNullOrEmpty(c) || !IsDigits(c)) continue;
                if (!ulong.TryParse(c, out var hash)) continue;
                if (!recovered.TryGetValue(hash, out var path)) continue;
                req.Container[i] = path;
                patched++;
            }
            return patched;
        }
    }
}
