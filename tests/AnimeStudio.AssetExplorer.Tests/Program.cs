using System.Diagnostics;
using System.Text;
using AnimeStudio;
using AnimeStudio.AssetExplorer;
using Newtonsoft.Json;

try
{
if (IndexWorker.TryRun(args)) return;
var output = Path.GetFullPath(args.Length > 1 ? args[1] : "asset-explorer-test-output");
Directory.CreateDirectory(output);
void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }

if (args.FirstOrDefault() == "--optimize-fbx")
{
    var target = Path.Combine(output, Path.GetFileName(args[2]));
    File.Copy(args[2], target, false);
    var result = FbxBinaryOptimizer.Optimize(target);
    Console.WriteLine(JsonConvert.SerializeObject(result));
    var before = FbxInspection.Read(args[2]); var after = FbxInspection.Read(target);
    Check(before.Models.SequenceEqual(after.Models) && before.Animations.SequenceEqual(after.Animations), "optimization retains all models and all animation takes");
    Check(result.OptimizedBytes < result.OriginalBytes, "real FBX became smaller");
    var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target));
    FbxBinaryOptimizer.Optimize(target);
    Check(hash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target))), "lossless compaction is idempotent");
    return;
}

if (args.FirstOrDefault() == "--layout")
{
    Exception failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
            System.Windows.Forms.Application.EnableVisualStyles();
            using var form = new ExplorerForm(new PreviewBridge(), false) { Opacity = 0, ShowInTaskbar = false };
            form.Show(); form.PerformLayout(); System.Windows.Forms.Application.DoEvents();
            using var image = new System.Drawing.Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
            image.Save(Path.Combine(output, "explorer.png"));
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    if (failure != null) throw failure;
    Check(true, "Explorer form renders"); return;
}

if (args.FirstOrDefault() == "--directory-layout")
{
    Exception failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
            System.Windows.Forms.Application.EnableVisualStyles();
            var builder = new AssetIndexStore.Builder();
            builder.Add(new CatalogAsset { Name = "Hero", Type = ClassIDType.Animator, Source = "demo.blk", Container = "Assets/Characters/Hero/Model/Hero.fbx", PathID = 1 });
            builder.Add(new CatalogAsset { Name = "Idle", Type = ClassIDType.AnimationClip, Source = "demo.blk", Container = "Assets/Characters/Hero/Animation/Idle.fbx", PathID = 2 });
            builder.Add(new CatalogAsset { Name = "Walk", Type = ClassIDType.AnimationClip, Source = "demo.blk", Container = "Assets/Characters/Hero/Animation/Walk.fbx", PathID = 3 });
            builder.Add(new CatalogAsset { Name = "Diffuse", Type = ClassIDType.Texture2D, Source = "demo.blk", Container = "Assets/Characters/Hero/Textures/Diffuse.png", PathID = 4 });
            builder.Add(new CatalogAsset { Name = "Info", Type = ClassIDType.TextAsset, Source = "demo.blk", Container = "Assets/Characters/Hero/info.json", PathID = 5 });
            var catalog = builder.Build(GameType.ZZZ, "fixture");
            using var form = new DirectoryExportForm(catalog, new PreviewBridge(), new(GameType.ZZZ, [], "", ""), "Assets/Characters/Hero", output)
                { Opacity = 0, ShowInTaskbar = false };
            form.Show();
            var button = (System.Windows.Forms.Button)form.Controls.Find("collectDirectory", true).Single();
            button.PerformClick();
            var timer = Stopwatch.StartNew();
            while (!button.Enabled && timer.ElapsedMilliseconds < 5000) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(5); }
            Check(((System.Windows.Forms.ListView)form.Controls.Find("resourcePreview", true).Single()).VirtualListSize == 5, "directory dialog collects and previews primary resources");
            var tree = (System.Windows.Forms.TreeView)form.Controls.Find("directoryTree", true).Single();
            var typeList = (System.Windows.Forms.CheckedListBox)form.Controls.Find("fileTypes", true).Single();
            var preview = (System.Windows.Forms.ListView)form.Controls.Find("resourcePreview", true).Single();
            tree.Nodes[0].Nodes.Cast<System.Windows.Forms.TreeNode>().Single(n => n.Text == "Model").Checked = false;
            Check(preview.VirtualListSize == 4, "unchecking one directory preserves other selections");
            for (int i = 0; i < typeList.Items.Count; i++) typeList.SetItemChecked(i, typeList.Items[i].ToString().StartsWith(".fbx"));
            System.Windows.Forms.Application.DoEvents();
            Check(preview.VirtualListSize == 2, "directory and file-type multiselect intersect");
            form.PerformLayout();
            using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(Path.Combine(output, "directory.png"));
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    if (failure != null) throw failure;
    return;
}

if (args.FirstOrDefault() == "--benchmark")
{
    var timer = Stopwatch.StartNew();
    var store = AssetIndexStore.Load(args[2], output, Console.WriteLine, CancellationToken.None);
    Console.WriteLine($"BENCH load {store.Count:N0} rows: {timer.ElapsedMilliseconds} ms; memory {GC.GetTotalMemory(false) / 1048576} MB");
    foreach (var query in new[] { new AssetIndexStore.Query("Anby", "全部类型", "", false),
        new AssetIndexStore.Query("Anby", "Animator", "", false), new AssetIndexStore.Query("Palicus", "AnimationClip", "", false) })
    {
        timer.Restart(); var hits = store.Search(query, CancellationToken.None);
        Console.WriteLine($"BENCH search {query}: {timer.ElapsedMilliseconds} ms, {hits.Length} hits");
        foreach (var i in hits.Take(2)) Console.WriteLine(JsonConvert.SerializeObject(store.Entry(i)));
        if (query.Type == "AnimationClip" && hits.Length > 0)
            File.WriteAllText(Path.Combine(output, "animation-sample.json"), JsonConvert.SerializeObject(store.Entry(hits[0])));
    }
    return;
}

if (args.FirstOrDefault() == "--catalog-find")
{
    var store = AssetIndexStore.Load(args[2], output, Console.WriteLine, default);
    var hits = store.Search(new(args[3], args.Length > 4 ? args[4] : "全部类型", "", false), default);
    var rows = hits.Select(store.Entry).ToArray();
    File.WriteAllText(Path.Combine(output, "matches.json"), JsonConvert.SerializeObject(rows, Formatting.Indented));
    Console.WriteLine($"MATCHES {rows.Length}: " + string.Join(", ", rows.GroupBy(r => r.Type).Select(g => $"{g.Key}={g.Count()}")));
    foreach (var row in rows.Take(12)) Console.WriteLine(JsonConvert.SerializeObject(row));
    return;
}

if (args.FirstOrDefault() == "--probe-acl")
{
    if (args.Length > 6 && args[6] == "--legacy") USE_ZZZ_ACL_V2.enabled = false;
    var probeStore = AssetIndexStore.Load(args[2], output, Console.WriteLine, default);
    foreach (var probeDictionary in new[] { "Z3-AssetIndex-Eleiyas.json", "Z3-AssetIndex-Recovered.json" })
    {
        var probeDictionaryPath = Path.Combine(Path.GetDirectoryName(args[3]), probeDictionary);
        if (File.Exists(probeDictionaryPath)) probeStore.ApplyPathDict(JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(probeDictionaryPath)));
    }
    var probePlan = probeStore.CollectDirectory(args[4], true, default);
    var probeTarget = probePlan.Groups.Single(g => Path.GetFileName(g.ResourcePath).Equals(args[5], StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"GROUP {probeTarget.ResourcePath} members={probeTarget.Members.Length}: " + string.Join(", ", probeTarget.Members.Select(m => m.Name)));
    var probeContext = new CatalogRequest(probeStore.Game, probeTarget.Members, args[3], "");
    var probeMap = CabCatalog.Read(probeContext.CabMap, default);
    var probeRequest = probeContext with { Assets = probeTarget.Members };
    var probeLoad = CabCatalog.Plan(probeRequest, default, probeMap);
    if (probeLoad.Missing.Length > 0) throw new FileNotFoundException(string.Join(Environment.NewLine, probeLoad.Missing));
    var probeManager = new AssetsManager { Game = GameManager.GetGameByType(probeRequest.Game), ResolveDependencies = false, FilterData = new() { Items = probeLoad.Offsets.ToList() } };
    probeManager.LoadFiles(probeLoad.Files, mergeSplitAssets: false);
    probeManager.FilterData.Items.Clear();
    var probeContent = PrimaryAssetResolver.Resolve(probeManager, probeTarget, probeLoad.Selected);
    var probeDump = new List<string>();
    foreach (var probeClip in probeContent.Clips)
    {
        var pClip = probeClip.m_MuscleClip.m_Clip;
        var pAcl = pClip.m_ACLClip;
        probeDump.Add($"clip {probeClip.Name}: aclType={pAcl.GetType().Name} isSet={pAcl.IsSet} curves={pAcl.CurveCount} streamed={pClip.m_StreamedClip?.data?.Length ?? 0} dense={pClip.m_DenseClip?.m_CurveCount ?? 0} constant={pClip.m_ConstantClip?.data?.Length ?? 0}");
        probeDump.Add($"  timing: sampleRate={probeClip.m_SampleRate} muscleStart={probeClip.m_MuscleClip.m_StartTime} muscleStop={probeClip.m_MuscleClip.m_StopTime} loopTime={probeClip.m_MuscleClip.m_LoopTime}");
        if (pAcl is MHYACLClip pMhy)
        {
            probeDump.Add($"  clipData={pMhy.m_ClipData?.Length ?? 0} curveCount={pMhy.m_CurveCount} constCurveCount={pMhy.m_ConstCurveCount} dbMeta={pMhy.m_databaseData?.Length ?? 0}");
            if (pMhy.m_ClipData != null && pMhy.m_ClipData.Length >= 32)
            {
                AclProbe.DumpBuffer(probeDump, "  buf0", pMhy.m_ClipData, 0);
                var probeSize0 = (int)BitConverter.ToUInt32(pMhy.m_ClipData, 0);
                AclProbe.DumpBuffer(probeDump, "  buf1", pMhy.m_ClipData, 16 * ((probeSize0 + 15) / 16));
            }
        }
        if (pAcl is ZZZACLClip pZzz) probeDump.Add($"  bulkData={pZzz.m_DatabaseData?.Length ?? 0}");
    }
    if (probeContent.Clips.Length == 0) probeDump.Add("NO CLIPS RESOLVED");
    foreach (var probeLine in probeDump) Console.WriteLine(probeLine);
    File.WriteAllLines(Path.Combine(output, "acl-dump.txt"), probeDump);
    Console.Out.Flush();
    Logger.Silent = true;
    foreach (var probeClip in probeContent.Clips)
    {
        var pAcl = probeClip.m_MuscleClip.m_Clip.m_ACLClip;
        if (!pAcl.IsSet) { Console.WriteLine($"SKIP {probeClip.Name} (no ACL data)"); continue; }
        Console.WriteLine($"PROCESS {probeClip.Name} ...");
        Console.Out.Flush();
        try
        {
            pAcl.Process(probeManager.Game, out var probeValues, out var probeTimes);
            Console.WriteLine($"DECODED {probeClip.Name}: times={probeTimes.Length} values={probeValues.Length} curves={pAcl.CurveCount}");
            if (pAcl is MHYACLClip rqClip && rqClip.m_ClipData != null && rqClip.m_ClipData.Length >= 32 && probeTimes.Length > 1)
            {
                var rqTracks = (int)BitConverter.ToUInt32(rqClip.m_ClipData, 16);
                var rqStride = (int)pAcl.CurveCount;
                if (rqTracks * 10 <= rqStride && (long)rqStride * probeTimes.Length == probeValues.LongLength)
                {
                    double rqDot = 0; long rqPairs = 0, rqBadNorm = 0;
                    for (var rqT = 0; rqT + 1 < probeTimes.Length; rqT++)
                        for (var rqK = 0; rqK < rqTracks; rqK++)
                        {
                            var qa = rqT * rqStride + rqK * 10; var qb = qa + rqStride;
                            double ax = probeValues[qa], ay = probeValues[qa + 1], az = probeValues[qa + 2], aw = probeValues[qa + 3];
                            double bx = probeValues[qb], by = probeValues[qb + 1], bz = probeValues[qb + 2], bw = probeValues[qb + 3];
                            if (Math.Abs(Math.Sqrt(ax * ax + ay * ay + az * az + aw * aw) - 1) > 0.01) rqBadNorm++;
                            rqDot += Math.Abs(ax * bx + ay * by + az * bz + aw * bw); rqPairs++;
                        }
                    Console.WriteLine($"  rotation continuity: mean |dot(q_t, q_t+1)| = {rqDot / Math.Max(1, rqPairs):F4} over {rqPairs} pairs, non-unit quats = {rqBadNorm}, transform tracks = {rqTracks}");
                    Console.WriteLine($"  track0 f0 q=({probeValues[0]:F3}, {probeValues[1]:F3}, {probeValues[2]:F3}, {probeValues[3]:F3}) pos=({probeValues[4]:F3}, {probeValues[5]:F3}, {probeValues[6]:F3}) scale=({probeValues[7]:F3}, {probeValues[8]:F3}, {probeValues[9]:F3}); f1 q=({probeValues[rqStride]:F3}, {probeValues[rqStride + 1]:F3}, {probeValues[rqStride + 2]:F3}, {probeValues[rqStride + 3]:F3})");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"DECODE FAILED {probeClip.Name}: {ex.GetBaseException().Message}"); }
        Console.Out.Flush();
    }
    return;
}

if (args.FirstOrDefault() == "--probe-mesh")
{
    var pmStore = AssetIndexStore.Load(args[2], output, Console.WriteLine, default);
    foreach (var pmDictionary in new[] { "Z3-AssetIndex-Eleiyas.json", "Z3-AssetIndex-Recovered.json" })
    {
        var pmDictionaryPath = Path.Combine(Path.GetDirectoryName(args[3]), pmDictionary);
        if (File.Exists(pmDictionaryPath)) pmStore.ApplyPathDict(JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(pmDictionaryPath)));
    }
    var pmPlan = pmStore.CollectDirectory(args[4], true, default);
    var pmTarget = pmPlan.Groups.Single(g => Path.GetFileName(g.ResourcePath).Equals(args[5], StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"GROUP {pmTarget.ResourcePath} members={pmTarget.Members.Length}: " + string.Join(", ", pmTarget.Members.Select(m => $"{m.Type}:{m.Name}")));
    var pmContext = new CatalogRequest(pmStore.Game, pmTarget.Members, args[3], "") { MeshCatalog = pmStore };
    var pmMap = CabCatalog.Read(pmContext.CabMap, default);
    var pmLoad = CabCatalog.Plan(pmContext, default, pmMap);
    if (pmLoad.Missing.Length > 0) throw new FileNotFoundException(string.Join(Environment.NewLine, pmLoad.Missing));
    var pmManager = new AssetsManager { Game = GameManager.GetGameByType(pmContext.Game), ResolveDependencies = false, FilterData = new() { Items = pmLoad.Offsets.ToList() } };
    pmManager.LoadFiles(pmLoad.Files, mergeSplitAssets: false);
    pmManager.FilterData.Items.Clear();
    var pmRegex = new System.Text.RegularExpressions.Regex("Assets[/" + (char)92 + "][ !#-~]{1,2048}?[.][A-Za-z0-9]{1,8}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    foreach (var pmFile in pmManager.assetsFileList)
        foreach (var pmGo in pmFile.Objects.OfType<GameObject>())
        {
            var pmParent = pmGo.m_Transform?.m_Father.TryGet(out var pmParentT) == true && pmParentT.m_GameObject.TryGet(out var pmParentGo) ? pmParentGo.Name : "<root>";
            var pmRenderer = (AnimeStudio.Object)pmGo.m_SkinnedMeshRenderer ?? pmGo.m_MeshFilter;
            string pmState = pmRenderer == null ? "" : pmRenderer switch
            {
                SkinnedMeshRenderer smr => smr.m_Mesh.IsNull ? " SMR:mesh=NULL" : " SMR:mesh=set",
                MeshFilter mf => mf.m_Mesh.IsNull ? " MF:mesh=NULL" : " MF:mesh=set",
                _ => ""
            };
            var pmBehaviours = new List<string>();
            foreach (var mbPtr in pmGo.m_Components)
            {
                if (!mbPtr.TryGet<MonoBehaviour>(out var mb)) continue;
                var mbClass = mb.m_Script.TryGet(out var mbScript) ? mbScript.m_ClassName : "?";
                var mbRaw = mb.GetRawData();
                var mbHits = pmRegex.Matches(System.Text.Encoding.UTF8.GetString(mbRaw)).Select(m => m.Value.Replace((char)92, '/')).Distinct().ToArray();
                pmBehaviours.Add($"{mbClass}/{mb.Name}[{mbRaw.Length}B]" + (mbHits.Length > 0 ? " paths=" + string.Join(" | ", mbHits) : ""));
            }
            if (pmRenderer != null || pmBehaviours.Count > 0)
                Console.WriteLine($"GO {pmGo.Name} (parent={pmParent}){pmState}" + (pmBehaviours.Count > 0 ? " MB: " + string.Join(" ; ", pmBehaviours) : ""));
        }
    var pmBindings = SeparateMeshSupport.FindBindings(pmManager, pmLoad.Selected, default);
    Console.WriteLine($"BINDINGS {pmBindings.Count}");
    var pmCandidates = new List<string>();
    foreach (var pmBinding in pmBindings)
    {
        var pmSlash = pmBinding.Path.LastIndexOf('/');
        var pmDir = pmBinding.Path[..pmSlash]; var pmFileName = pmBinding.Path[(pmSlash + 1)..];
        var pmModel = pmDir[(pmDir.LastIndexOf('/') + 1)..];
        var pmPrefixed = pmFileName.StartsWith("SeparateMesh_", StringComparison.OrdinalIgnoreCase) ? pmBinding.Path : $"{pmDir}/SeparateMesh_{pmModel}_{pmFileName}";
        var pmPlain = pmFileName.StartsWith("SeparateMesh_", StringComparison.OrdinalIgnoreCase) ? $"{pmDir}/{pmBinding.Component.Name}.mesh" : pmBinding.Path;
        Console.WriteLine($"  binding {pmBinding.Component.Type}:{pmBinding.Component.Name} -> {pmBinding.Path}");
        pmCandidates.Add(pmPlain); pmCandidates.Add(pmPrefixed);
    }
    var pmFound = pmStore.FindMeshes(pmCandidates.Distinct(), default);
    foreach (var pmCandidate in pmCandidates.Distinct())
    {
        var pmRows = pmFound.GetValueOrDefault(pmCandidate) ?? [];
        Console.WriteLine($"  lookup {Path.GetFileName(pmCandidate)}: {pmRows.Length} rows" + (pmRows.Length > 0 ? " -> " + string.Join(" | ", pmRows.Select(r => $"{r.Name}#{r.PathID}@{Path.GetFileName(r.Source)}:{r.Offset}")) : ""));
    }
    return;
}

if (args.FirstOrDefault() == "--probe-pose")
{
    var ppStore = AssetIndexStore.Load(args[2], output, Console.WriteLine, default);
    foreach (var ppDictionary in new[] { "Z3-AssetIndex-Eleiyas.json", "Z3-AssetIndex-Recovered.json" })
    {
        var ppDictionaryPath = Path.Combine(Path.GetDirectoryName(args[3]), ppDictionary);
        if (File.Exists(ppDictionaryPath)) ppStore.ApplyPathDict(JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(ppDictionaryPath)));
    }
    var ppPlan = ppStore.CollectDirectory(args[4], true, default);
    var ppTarget = ppPlan.Groups.Single(g => Path.GetFileName(g.ResourcePath).Equals(args[5], StringComparison.OrdinalIgnoreCase));
    var ppContext = new CatalogRequest(ppStore.Game, ppTarget.Members, args[3], "");
    var ppMap = CabCatalog.Read(ppContext.CabMap, default);
    var ppLoad = CabCatalog.Plan(ppContext, default, ppMap);
    if (ppLoad.Missing.Length > 0) throw new FileNotFoundException(string.Join(Environment.NewLine, ppLoad.Missing));
    var ppManager = new AssetsManager { Game = GameManager.GetGameByType(ppContext.Game), ResolveDependencies = false, FilterData = new() { Items = ppLoad.Offsets.ToList() } };
    ppManager.LoadFiles(ppLoad.Files, mergeSplitAssets: false);
    ppManager.FilterData.Items.Clear();
    var ppContent = PrimaryAssetResolver.Resolve(ppManager, ppTarget, ppLoad.Selected);
    Logger.Silent = true;
    var ppOptions = new ModelConverter.Options { game = ppManager.Game, collectAnimations = false, imageFormat = ImageFormat.Png, exportMaterials = false, materials = new HashSet<Material>(), uvs = new(), texs = new() };
    var ppModel = ppContent.Roots.Length == 1
        ? new ModelConverter(ppContent.Roots[0], ppOptions, ppContent.Clips)
        : new ModelConverter(Path.GetFileNameWithoutExtension(ppTarget.ResourcePath), ppContent.Roots.ToList(), ppOptions, ppContent.Clips);
    Console.WriteLine($"MODEL {Path.GetFileName(ppTarget.ResourcePath)}: roots={ppContent.Roots.Length} clips={ppContent.Clips.Length} animations={ppModel.AnimationList.Count}");
    foreach (var ppAnim in ppModel.AnimationList)
    {
        var ppRows = new List<(double Angle, double Dist, string Path)>();
        var ppUnmatched = 0;
        foreach (var ppTrack in ppAnim.TrackList)
        {
            if (string.IsNullOrEmpty(ppTrack.Path) || ppTrack.Rotations.Count == 0) continue;
            var ppFrame = ppModel.RootFrame.FindFrameByPath(ppTrack.Path);
            if (ppFrame == null) { ppUnmatched++; continue; }
            var ppQ = ppTrack.Rotations[0].value; var ppB = ppFrame.LocalRotation;
            var ppDot = Math.Abs(ppQ.X * ppB.X + ppQ.Y * ppB.Y + ppQ.Z * ppB.Z + ppQ.W * ppB.W);
            var ppAngle = 2 * Math.Acos(Math.Min(1.0, ppDot)) * 180 / Math.PI;
            var ppDist = -1.0;
            if (ppTrack.Translations.Count > 0)
            {
                var ppP = ppTrack.Translations[0].value; var ppC = ppFrame.LocalPosition;
                ppDist = Math.Sqrt((ppP.X - ppC.X) * (ppP.X - ppC.X) + (ppP.Y - ppC.Y) * (ppP.Y - ppC.Y) + (ppP.Z - ppC.Z) * (ppP.Z - ppC.Z));
            }
            ppRows.Add((ppAngle, ppDist, ppTrack.Path));
        }
        if (ppRows.Count == 0) { Console.WriteLine($"ANIM {ppAnim.Name}: no rotation tracks matched (unmatched={ppUnmatched})"); continue; }
        var ppSorted = ppRows.OrderBy(ppR => ppR.Angle).ToList();
        var ppMean = ppRows.Average(ppR => ppR.Angle); var ppMedian = ppSorted[ppSorted.Count / 2].Angle;
        var ppUnder15 = ppRows.Count(ppR => ppR.Angle < 15) * 100.0 / ppRows.Count; var ppOver90 = ppRows.Count(ppR => ppR.Angle > 90) * 100.0 / ppRows.Count;
        var ppDists = ppRows.Where(ppR => ppR.Dist >= 0).Select(ppR => ppR.Dist).ToList();
        Console.WriteLine($"ANIM {ppAnim.Name}: tracks={ppRows.Count} unmatched={ppUnmatched} | frame0 vs bind rotation: mean={ppMean:F1} median={ppMedian:F1} under15deg={ppUnder15:F0}% over90deg={ppOver90:F0}% | translation dist mean={(ppDists.Count > 0 ? ppDists.Average() : -1):F4} max={(ppDists.Count > 0 ? ppDists.Max() : -1):F4}");
        foreach (var ppR in ppSorted.TakeLast(3)) Console.WriteLine($"    worst {ppR.Angle:F1}deg {ppR.Path.Split('/').Last()}");
        var ppScaleF0 = new List<(double Diff, string Path)>(); double ppSMin = double.MaxValue, ppSMax = double.MinValue; var ppWildScaleTracks = 0;
        foreach (var ppTrack in ppAnim.TrackList)
        {
            if (string.IsNullOrEmpty(ppTrack.Path) || ppTrack.Scalings.Count == 0) continue;
            var ppFrame2 = ppModel.RootFrame.FindFrameByPath(ppTrack.Path);
            var ppWild = false;
            foreach (var ppKey in ppTrack.Scalings)
            {
                var ppV = ppKey.value;
                ppSMin = Math.Min(ppSMin, Math.Min(ppV.X, Math.Min(ppV.Y, ppV.Z))); ppSMax = Math.Max(ppSMax, Math.Max(ppV.X, Math.Max(ppV.Y, ppV.Z)));
                if (Math.Abs(ppV.X - 1) > 0.1 || Math.Abs(ppV.Y - 1) > 0.1 || Math.Abs(ppV.Z - 1) > 0.1) ppWild = true;
            }
            if (ppWild) ppWildScaleTracks++;
            if (ppFrame2 != null)
            {
                var ppS0 = ppTrack.Scalings[0].value; var ppBs = ppFrame2.LocalScale;
                ppScaleF0.Add((Math.Max(Math.Abs(ppS0.X - ppBs.X), Math.Max(Math.Abs(ppS0.Y - ppBs.Y), Math.Abs(ppS0.Z - ppBs.Z))), ppTrack.Path));
            }
        }
        if (ppScaleF0.Count > 0) Console.WriteLine($"    scale: tracks={ppScaleF0.Count} frame0 vs node max diff mean={ppScaleF0.Average(ppX => ppX.Diff):F4} worst={ppScaleF0.Max(ppX => ppX.Diff):F4} | all-frame scale range [{ppSMin:F3}, {ppSMax:F3}] tracks deviating over 10% from 1: {ppWildScaleTracks}");
        var pqMotion = new List<(double MaxAngle, double MaxMove, string Path)>();
        foreach (var pqTrack in ppAnim.TrackList)
        {
            if (string.IsNullOrEmpty(pqTrack.Path) || pqTrack.Rotations.Count == 0) continue;
            var pqQ0 = pqTrack.Rotations[0].value; double pqMaxAngle = 0;
            foreach (var pqKey in pqTrack.Rotations)
            {
                var pqQ = pqKey.value;
                var pqDot = Math.Abs(pqQ.X * pqQ0.X + pqQ.Y * pqQ0.Y + pqQ.Z * pqQ0.Z + pqQ.W * pqQ0.W);
                pqMaxAngle = Math.Max(pqMaxAngle, 2 * Math.Acos(Math.Min(1.0, pqDot)) * 180 / Math.PI);
            }
            double pqMaxMove = 0;
            if (pqTrack.Translations.Count > 0)
            {
                var pqP0 = pqTrack.Translations[0].value;
                foreach (var pqKey in pqTrack.Translations) { var pqP = pqKey.value; pqMaxMove = Math.Max(pqMaxMove, Math.Sqrt((pqP.X - pqP0.X) * (pqP.X - pqP0.X) + (pqP.Y - pqP0.Y) * (pqP.Y - pqP0.Y) + (pqP.Z - pqP0.Z) * (pqP.Z - pqP0.Z))); }
            }
            pqMotion.Add((pqMaxAngle, pqMaxMove, pqTrack.Path));
        }
        if (pqMotion.Count > 0)
        {
            Console.WriteLine($"    motion: tracks={pqMotion.Count} rot>5deg={pqMotion.Count(pqA => pqA.MaxAngle > 5)} rot>30deg={pqMotion.Count(pqA => pqA.MaxAngle > 30)} rot>90deg={pqMotion.Count(pqA => pqA.MaxAngle > 90)} pos>0.01={pqMotion.Count(pqA => pqA.MaxMove > 0.01)}");
            foreach (var pqTop in pqMotion.OrderByDescending(pqB => pqB.MaxAngle).Take(6)) Console.WriteLine($"      top {pqTop.MaxAngle:F1}deg move={pqTop.MaxMove:F3} {pqTop.Path.Split('/').Last()}");
        }
    }
    return;
}

if (args.FirstOrDefault() == "--probe-skeleton")
{
    var psStore = AssetIndexStore.Load(args[2], output, Console.WriteLine, default);
    foreach (var psDictionary in new[] { "Z3-AssetIndex-Eleiyas.json", "Z3-AssetIndex-Recovered.json" })
    {
        var psDictionaryPath = Path.Combine(Path.GetDirectoryName(args[3]), psDictionary);
        if (File.Exists(psDictionaryPath)) psStore.ApplyPathDict(JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(psDictionaryPath)));
    }
    var psPlan = psStore.CollectDirectory(args[4], true, default);
    var psTarget = psPlan.Groups.Single(g => Path.GetFileName(g.ResourcePath).Equals(args[5], StringComparison.OrdinalIgnoreCase));
    var psContext = new CatalogRequest(psStore.Game, psTarget.Members, args[3], "");
    var psMap = CabCatalog.Read(psContext.CabMap, default);
    var psLoad = CabCatalog.Plan(psContext, default, psMap);
    if (psLoad.Missing.Length > 0) throw new FileNotFoundException(string.Join(Environment.NewLine, psLoad.Missing));
    var psManager = new AssetsManager { Game = GameManager.GetGameByType(psContext.Game), ResolveDependencies = false, FilterData = new() { Items = psLoad.Offsets.ToList() } };
    psManager.LoadFiles(psLoad.Files, mergeSplitAssets: false);
    psManager.FilterData.Items.Clear();
    var psContent = PrimaryAssetResolver.Resolve(psManager, psTarget, psLoad.Selected);
    var psLines = new List<string>();
    void PsWalk(GameObject psGo, string psPath)
    {
        var psT = psGo.m_Transform;
        if (psT == null) return;
        var psP = psT.m_LocalPosition; var psQ = psT.m_LocalRotation; var psS = psT.m_LocalScale;
        psLines.Add($"{psPath}|{psP.X:R}|{psP.Y:R}|{psP.Z:R}|{psQ.X:R}|{psQ.Y:R}|{psQ.Z:R}|{psQ.W:R}|{psS.X:R}|{psS.Y:R}|{psS.Z:R}");
        foreach (var psChildPtr in psT.m_Children)
            if (psChildPtr.TryGet(out var psChildT) && psChildT.m_GameObject.TryGet(out var psChildGo)) PsWalk(psChildGo, psPath + "/" + psChildGo.Name);
    }
    foreach (var psRoot in psContent.Roots) PsWalk(psRoot, psRoot.Name);
    File.WriteAllLines(args[6], psLines);
    Console.WriteLine($"SKELETON {Path.GetFileName(psTarget.ResourcePath)}: roots={psContent.Roots.Length} nodes={psLines.Count}");
    return;
}

if (args.FirstOrDefault() == "--probe-skin")
{
    var pkStore = AssetIndexStore.Load(args[2], output, Console.WriteLine, default);
    foreach (var pkDictionary in new[] { "Z3-AssetIndex-Eleiyas.json", "Z3-AssetIndex-Recovered.json" })
    {
        var pkDictionaryPath = Path.Combine(Path.GetDirectoryName(args[3]), pkDictionary);
        if (File.Exists(pkDictionaryPath)) pkStore.ApplyPathDict(JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(pkDictionaryPath)));
    }
    var pkPlan = pkStore.CollectDirectory(args[4], true, default);
    var pkTarget = pkPlan.Groups.Single(g => Path.GetFileName(g.ResourcePath).Equals(args[5], StringComparison.OrdinalIgnoreCase));
    var pkContext = new CatalogRequest(pkStore.Game, pkTarget.Members, args[3], "") { MeshCatalog = pkStore };
    var pkMap = CabCatalog.Read(pkContext.CabMap, default);
    var pkLoad = CabCatalog.Plan(pkContext, default, pkMap);
    if (pkLoad.Missing.Length > 0) throw new FileNotFoundException(string.Join(Environment.NewLine, pkLoad.Missing));
    var pkManager = new AssetsManager { Game = GameManager.GetGameByType(pkContext.Game), ResolveDependencies = false, FilterData = new() { Items = pkLoad.Offsets.ToList() } };
    pkManager.LoadFiles(pkLoad.Files, mergeSplitAssets: false);
    pkManager.FilterData.Items.Clear();
    var pkContent = PrimaryAssetResolver.Resolve(pkManager, pkTarget, pkLoad.Selected);
    var pkWithRoots = pkLoad.Selected.Concat(pkContent.Roots.Select(PrimaryAssetResolver.Reference)).Distinct().ToArray();
    Logger.Silent = true;
    var pkBound = SeparateMeshSupport.CompleteLoad(pkManager, pkContext, pkWithRoots, null, default, pkMap);
    pkContent = PrimaryAssetResolver.Resolve(pkManager, pkTarget, pkLoad.Selected);
    Console.WriteLine($"SKIN {Path.GetFileName(pkTarget.ResourcePath)}: separate meshes bound={pkBound} roots={pkContent.Roots.Length}");
    static uint PkCrc(string pkText)
    {
        uint pkC = 0xFFFFFFFF;
        foreach (var pkB in System.Text.Encoding.UTF8.GetBytes(pkText))
        {
            pkC ^= pkB;
            for (var pkK = 0; pkK < 8; pkK++) pkC = (pkC & 1) != 0 ? (pkC >> 1) ^ 0xEDB88320u : pkC >> 1;
        }
        return ~pkC;
    }
    var pkHashNoRoot = new Dictionary<uint, string>(); var pkHashWithRoot = new Dictionary<uint, string>();
    void PkWalk(GameObject pkGo, string pkRel, string pkFull)
    {
        if (pkGo.m_Transform == null) return;
        if (pkRel.Length > 0) pkHashNoRoot[PkCrc(pkRel)] = pkRel;
        pkHashWithRoot[PkCrc(pkFull)] = pkFull;
        foreach (var pkPtr in pkGo.m_Transform.m_Children)
            if (pkPtr.TryGet(out var pkChildT) && pkChildT.m_GameObject.TryGet(out var pkChildGo))
                PkWalk(pkChildGo, pkRel.Length == 0 ? pkChildGo.Name : pkRel + "/" + pkChildGo.Name, pkFull + "/" + pkChildGo.Name);
    }
    foreach (var pkRoot in pkContent.Roots) PkWalk(pkRoot, "", pkRoot.Name);
    string PkPathOf(Transform pkT)
    {
        var pkNames = new List<string>(); var pkCur = pkT;
        while (pkCur != null)
        {
            pkCur.m_GameObject.TryGet(out var pkG);
            var pkHasFather = pkCur.m_Father.TryGet(out var pkFather);
            if (pkHasFather) pkNames.Insert(0, pkG?.Name ?? "?");
            pkCur = pkHasFather ? pkFather : null;
        }
        return string.Join("/", pkNames);
    }
    foreach (var pkFile in pkManager.assetsFileList)
        foreach (var pkSmr in pkFile.Objects.OfType<SkinnedMeshRenderer>())
        {
            pkSmr.m_GameObject.TryGet(out var pkSmrGo);
            if (!pkSmr.m_Mesh.TryGet(out var pkMesh)) { Console.WriteLine($"  SMR {pkSmrGo?.Name}: mesh unresolved"); continue; }
            var pkBones = pkSmr.m_Bones; var pkResolved = pkBones.Count(pkBp => pkBp.TryGet(out _));
            var pkHashes = pkMesh.m_BoneNameHashes ?? Array.Empty<uint>();
            var pkMaxIndex = pkMesh.m_Skin != null && pkMesh.m_Skin.Count > 0 ? pkMesh.m_Skin.Max(pkS => pkS.boneIndex.Max()) : -1;
            Console.WriteLine($"  SMR {pkSmrGo?.Name}: mesh={pkMesh.Name} bones={pkBones.Count} resolved={pkResolved} bindposes={pkMesh.m_BindPose?.Length ?? 0} boneHashes={pkHashes.Length} skinVerts={pkMesh.m_Skin?.Count ?? 0} maxBoneIndex={pkMaxIndex}");
            var pkN = Math.Min(pkBones.Count, pkHashes.Length); int pkMatch = 0, pkMismatch = 0; var pkExamples = new List<string>();
            for (var pkI = 0; pkI < pkN; pkI++)
            {
                if (!pkBones[pkI].TryGet(out var pkT)) continue;
                var pkRel = PkPathOf(pkT); var pkCrc = PkCrc(pkRel);
                if (pkCrc == pkHashes[pkI]) { pkMatch++; continue; }
                pkMismatch++;
                var pkMeshName = pkHashNoRoot.TryGetValue(pkHashes[pkI], out var pkMn) ? pkMn.Split('/').Last() : (pkHashWithRoot.TryGetValue(pkHashes[pkI], out var pkMn2) ? pkMn2.Split('/').Last() + "(with-root)" : "?");
                var pkWhere = Array.IndexOf(pkHashes, pkCrc);
                if (pkExamples.Count < 6) pkExamples.Add($"[{pkI}] smr bone={pkRel.Split('/').Last()} but mesh slot={pkMeshName}; smr bone appears at mesh index {pkWhere}");
            }
            var pkResolvable = pkHashes.Count(pkH => pkHashNoRoot.ContainsKey(pkH) || pkHashWithRoot.ContainsKey(pkH));
            Console.WriteLine($"      order check: index-match={pkMatch} mismatch={pkMismatch} | mesh bone hashes resolvable in skeleton={pkResolvable}/{pkHashes.Length}");
            foreach (var pkE in pkExamples) Console.WriteLine("      " + pkE);
        }
    return;
}

if (args.FirstOrDefault() == "--probe-avatar")
{
    Logger.Default = new ConsoleLogger();
    Logger.Silent = false; Logger.Flags = LoggerEvent.Warning | LoggerEvent.Error;
    var paStore = AssetIndexStore.Load(args[2], output, Console.WriteLine, default);
    var paDictionaryDirectory = args.Length > 6 && !args[6].StartsWith("--") ? args[6] : Path.GetDirectoryName(args[3]);
    foreach (var paDictionary in new[] { "Z3-AssetIndex-Eleiyas.json", "Z3-AssetIndex-Recovered.json" })
    {
        var paDictionaryPath = Path.Combine(paDictionaryDirectory, paDictionary);
        if (File.Exists(paDictionaryPath)) paStore.ApplyPathDict(JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(paDictionaryPath)));
    }
    var paPlan = paStore.CollectDirectory(args[4], true, default);
    var paMatches = paPlan.Groups.Where(g => Path.GetFileName(g.ResourcePath).Equals(args[5], StringComparison.OrdinalIgnoreCase)).ToArray();
    if (paMatches.Length == 0)
    {
        Console.WriteLine($"NO GROUP for {args[5]}; available:");
        foreach (var paGroupName in paPlan.Groups.Select(g => g.ResourcePath)) Console.WriteLine("  " + paGroupName);
        throw new InvalidOperationException("Avatar probe did not find the requested resource.");
    }
    var paTarget = paMatches[0];
    Console.WriteLine($"GROUP {paTarget.ResourcePath} members={paTarget.Members.Length}");
    foreach (var paMember in paTarget.Members)
        Console.WriteLine($"  member {paMember.Type}:{paMember.Name}#{paMember.PathID} @ {Path.GetFileName(paMember.Source)}:{paMember.Offset} cab={paMember.Cab}");
    var paContext = new CatalogRequest(paStore.Game, paTarget.Members, args[3], "");
    var paLoad = CabCatalog.Plan(paContext, default, CabCatalog.Read(paContext.CabMap, default));
    if (paLoad.Missing.Length > 0) Console.WriteLine("MISSING " + string.Join(Environment.NewLine, paLoad.Missing));
    Console.WriteLine("FILES " + string.Join(", ", paLoad.Files.Select(Path.GetFileName)));
    var paManager = new AssetsManager { Game = GameManager.GetGameByType(paContext.Game), ResolveDependencies = false, FilterData = new() { Items = paLoad.Offsets.ToList() } };
    try
    {
        paManager.LoadFiles(paLoad.Files, mergeSplitAssets: false);
        paManager.FilterData.Items.Clear();
        foreach (var paFile in paManager.assetsFileList)
        {
            Console.WriteLine($"FILE {Path.GetFileName(paFile.originalPath ?? paFile.fullName)} offset={paFile.offset} cab={paFile.fileName} objects={paFile.Objects.Count}");
            foreach (var paGroup in paFile.Objects.GroupBy(o => o.type).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  type {paGroup.Key}={paGroup.Count()}");
            foreach (var paObj in paFile.Objects.Where(o => paTarget.Members.Any(m => m.PathID == o.m_PathID)))
                Console.WriteLine($"  PATHID HIT {paObj.type}:{paObj.Name}#{paObj.m_PathID}");
            foreach (var paInfo in paFile.m_Objects.Where(o => paTarget.Members.Any(m => m.PathID == o.m_PathID)))
                Console.WriteLine($"  RAW HIT {(ClassIDType)paInfo.classID}#{paInfo.m_PathID} offset={paInfo.byteStart} size={paInfo.byteSize} typeHash={Convert.ToHexString(paInfo.serializedType.m_OldTypeHash)}");
        }
        var paFailures = 0;
        foreach (var paMember in paLoad.Selected)
        {
            try
            {
                var paFound = CabCatalog.Find(paManager, paMember);
                Console.WriteLine($"FIND OK {paMember.Type}:{paMember.Name} -> {paFound.Name}");
                if (paFound is Avatar paAvatar)
                    Console.WriteLine($"  AVATAR paths={paAvatar.m_TOS.Count} rootMotion={paAvatar.m_HumanDescription?.m_RootMotionBoneName}");
            }
            catch (Exception paError) { paFailures++; Console.WriteLine($"FIND FAIL {paMember.Type}:{paMember.Name} -> {paError.Message}"); }
        }
        Check(paFailures == 0, "all indexed primary-resource members loaded");
        if (args.Contains("--export"))
        {
            paContext = paContext with { MeshCatalog = paStore };
            var paContent = PrimaryAssetResolver.Resolve(paManager, paTarget, paLoad.Selected);
            var paWithRoots = paLoad.Selected.Concat(paContent.Roots.Select(PrimaryAssetResolver.Reference)).Distinct().ToArray();
            SeparateMeshSupport.CompleteLoad(paManager, paContext, paWithRoots, Console.WriteLine, default);
            paContent = PrimaryAssetResolver.Resolve(paManager, paTarget, paLoad.Selected);
            var paOptions = new ModelExportService.Options(new ModelConverter.Options
                { uvs = Enumerable.Range(0, 8).ToDictionary(i => "UV" + i, i => (i < 2, i < 2 ? i : 0)), texs = new() },
                new Fbx.ExportOptions { exportAllNodes = true, exportSkins = true, exportAnimations = true, exportBlendShape = true,
                    boneSize = 10, scaleFactor = 1, fbxVersion = 3, fbxFormat = 0 });
            var paResult = PrimaryFbxExporter.Export(paManager, paTarget, paContent, output, paOptions, default);
            Console.WriteLine(JsonConvert.SerializeObject(paResult));
            var paFbx = FbxInspection.Read(paResult.MainFiles.Single());
            Check(paFbx.Models.Any(m => m.Type == "Mesh"), "exported FBX contains meshes");
            Check(paFbx.Models.Any(m => m.Type == "LimbNode"), "exported FBX contains skeleton bones");
        }
    }
    finally { paManager.Clear(); }
    return;
}

if (args.FirstOrDefault() == "--worker-crash")
{
    var gui = System.Reflection.Assembly.LoadFrom(args[2]);
    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
    var bridgeType = gui.GetType("AnimeStudio.GUI.MainForm+ExplorerBridge", true);
    var options = bridgeType.GetMethod("GetModelOptions", flags | System.Reflection.BindingFlags.Static).Invoke(null, null);
    var clientType = gui.GetType("AnimeStudio.GUI.DirectoryExportWorker+Client", true);
    var client = Activator.CreateInstance(clientType, flags | System.Reflection.BindingFlags.Instance, null,
        new object[] { new CatalogRequest(GameType.ZZZ, [], args[3], ""), options }, null);
    var exportMethod = clientType.GetMethod("ExportAsync", flags | System.Reflection.BindingFlags.Instance);
    var processField = clientType.GetField("process", flags | System.Reflection.BindingFlags.Instance);
    Task<PrimaryExportResult> Begin() => (Task<PrimaryExportResult>)exportMethod.Invoke(client,
        new object[] { new PrimaryAssetGroup("Assets/Test/missing.fbx", []), output, CancellationToken.None });
    try
    {
        var first = Begin();
        var worker = (Process)processField.GetValue(client);
        Check(worker != null, "worker process started");
        worker.Kill(entireProcessTree: true);
        try { await first.WaitAsync(TimeSpan.FromSeconds(15)); throw new Exception("crash accepted"); }
        catch (IOException ex) { Check(ex.Message.Contains("意外退出"), "worker crash returns an error without hanging"); }
        try { await Begin().WaitAsync(TimeSpan.FromSeconds(30)); throw new Exception("empty fixture accepted"); }
        catch (IOException ex) { Check(!ex.Message.Contains("意外退出"), "replacement worker starts and processes the next job"); }
    }
    finally { ((IDisposable)client).Dispose(); }
    return;
}

if (args.FirstOrDefault() is "--directory" or "--directory-origin" or "--directory-attack")
{
    var store = AssetIndexStore.Load(args[2], output, Console.WriteLine, default);
    if (args.Length > 4)
        foreach (var dictionary in new[] { "Z3-AssetIndex-Eleiyas.json", "Z3-AssetIndex-Recovered.json" })
        {
            var dictionaryPath = Path.Combine(Path.GetDirectoryName(args[4]), dictionary);
            if (File.Exists(dictionaryPath)) store.ApplyPathDict(JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(dictionaryPath)));
        }
    var plan = store.CollectDirectory(args[3], true, default);
    if (args[0] == "--directory-origin") plan = plan with { Groups = plan.Groups.Where(g => g.ResourcePath.EndsWith("Remielle_Origin_Model.fbx")).ToArray() };
    if (args[0] == "--directory-attack") plan = plan with { Groups = plan.Groups.Where(g => g.ResourcePath.EndsWith("/Avatar_Female_Size02_Remielle_Origin_Ani_Attack_Normal_01.fbx")).ToArray() };
    Console.WriteLine($"DIRECTORY {plan.Groups.Length} primary files, {plan.MatchedRows} rows, {plan.DuplicateRows} duplicate rows");
    File.WriteAllText(Path.Combine(output, "collection.json"), JsonConvert.SerializeObject(plan, Formatting.Indented));
    if (args.Length > 4)
    {
        Logger.Silent = true;
        var gui = System.Reflection.Assembly.LoadFrom(args[5]);
        var bridgeType = gui.GetType("AnimeStudio.GUI.MainForm+ExplorerBridge", true);
        var bridge = (IStudioBridge)Activator.CreateInstance(bridgeType,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, new object[] { null }, null);
        var context = new CatalogRequest(store.Game, [], args[4], "") { MeshCatalog = store, ExportLogDirectory = Path.Combine(output, "logs"), ExportWorkers = args.Length > 6 ? int.Parse(args[6]) : 2 };
        var exportTimer = Stopwatch.StartNew();
        using var exportCancellation = new CancellationTokenSource();
        if (args.Length > 7) exportCancellation.CancelAfter(int.Parse(args[7]));
        var result = await bridge.ExportDirectoryAsync(plan, context, Path.Combine(output, "files"), Console.WriteLine, exportCancellation.Token);
        Console.WriteLine($"EXPORT BENCH workers={context.ExportWorkers} seconds={exportTimer.Elapsed.TotalSeconds:F2}");
        File.WriteAllText(Path.Combine(output, "directory-result.json"), JsonConvert.SerializeObject(result, Formatting.Indented));
        if (args.Length > 7)
        {
            Check(result.Cancelled && result.Pending > 0 && result.Failed == 0, "real worker cancellation returns unfinished resources without reporting failures");
            return;
        }
        Check(result.Succeeded + result.Skipped == plan.Groups.Length && result.Failed == 0 && result.Pending == 0, "every primary resource exported or explicitly skipped");
        if (plan.Groups.All(g => g.IsFbx)) Check(result.Succeeded == plan.Groups.Length, "all FBXs exported without skipping");
        var manifest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(result.ReportPath));
        Check(!Directory.Exists(result.OutputDirectory) || !Directory.GetFiles(result.OutputDirectory, "primary-resource.json", SearchOption.AllDirectories).Any(), "output has no per-resource metadata files");
        if (plan.Groups.All(g => g.IsFbx)) Check(!Directory.GetFiles(result.OutputDirectory, "*.json", SearchOption.AllDirectories).Any(), "FBX output has no JSON sidecars or reports");
        foreach (var item in manifest["Items"])
        {
            var path = (string)item["ResourcePath"];
            if ((string)item["Status"] != "success" || !path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
            Check(item["MainFiles"].Count() == 1, "one main FBX per resource path: " + path);
            var file = Path.Combine(result.OutputDirectory, (string)item["MainFiles"][0]);
            var details = FbxInspection.Read(file);
            Check(details.Roots.Single().Type == "Null", "scene container is not exported as an extra skeleton root");
            var expected = plan.Groups.Single(g => g.ResourcePath == path).Members.Where(a => a.Type == ClassIDType.AnimationClip).Select(a => a.Name).ToArray();
            Check(expected.All(name => details.Animations.Contains(name)), "subanimations embedded in their own FBX");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(file), "*.anim", SearchOption.AllDirectories).Any(), "no loose subasset animations");
            if (expected.Length > 0) Check(Encoding.ASCII.GetString(File.ReadAllBytes(file)).Contains("AnimationStack"), "FBX contains actual animation stacks");
        }
    }
    return;
}

if (args.FirstOrDefault() == "--integration")
{
    Logger.Silent = true;
    var source = args[2]; var cabMap = args[3];
    var selected = new List<CatalogAsset> { new() { Name = "Monster_Palicus", Type = ClassIDType.Animator,
        Source = source, PathID = 541663571111027022, Offset = 727482 } };
    if (args.Length > 4 && File.Exists(args[4])) selected.Add(JsonConvert.DeserializeObject<CatalogAsset>(File.ReadAllText(args[4])));
    var request = new CatalogRequest(GameType.ZZZ, selected.ToArray(), cabMap, "");
    var watch = Stopwatch.StartNew(); var plan = CabCatalog.Plan(request, CancellationToken.None);
    Check(plan.Missing.Length == 0, "all real CAB dependencies resolved: " + string.Join(", ", plan.Missing));
    Console.WriteLine($"INTEGRATION {plan.Files.Length} files, {plan.Offsets.Length} offsets, plan {watch.ElapsedMilliseconds} ms");
    var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ZZZ), ResolveDependencies = false,
        FilterData = new AssetsManager.AssetFilterData { Items = plan.Offsets.ToList() } };
    try
    {
        watch.Restart(); manager.LoadFiles(plan.Files, mergeSplitAssets: false); manager.FilterData.Items.Clear();
        foreach (var asset in plan.Selected) Check(CabCatalog.Find(manager, asset) != null, "exact asset loaded: " + asset.Name);
        Console.WriteLine($"INTEGRATION load {watch.ElapsedMilliseconds} ms, {manager.assetsFileList.Count} CABs");
        if (args.Length > 5)
        {
            var gui = System.Reflection.Assembly.LoadFrom(args[5]);
            var studio = gui.GetType("AnimeStudio.GUI.Studio", true);
            studio.GetField("assetsManager").SetValue(null, manager);
            studio.GetField("Game").SetValue(null, manager.Game);
            var built = studio.GetMethod("BuildAssetData").Invoke(null, null);
            var nodes = (List<System.Windows.Forms.TreeNode>)built.GetType().GetField("Item2").GetValue(built);
            IEnumerable<System.Windows.Forms.TreeNode> Flatten(IEnumerable<System.Windows.Forms.TreeNode> items)
            { foreach (var node in items) { yield return node; foreach (var child in Flatten(node.Nodes.Cast<System.Windows.Forms.TreeNode>())) yield return child; } }
            Check(Flatten(nodes).Any(node => node.Text == "Monster_Palicus"), "Studio Scene Hierarchy contains loaded model");
            var assets = (System.Collections.IEnumerable)studio.GetField("exportableAssets").GetValue(null);
            Check(assets.Cast<object>().Any(a => (ClassIDType)a.GetType().GetField("Type").GetValue(a) == ClassIDType.AnimationClip), "Studio Asset List contains loaded animation clips");
        }
        var options = new ModelExportService.Options(new ModelConverter.Options { uvs = Enumerable.Range(0, 8).ToDictionary(i => "UV" + i, i => (i < 2, i < 2 ? i : 0)), texs = new() },
            new Fbx.ExportOptions { exportAllNodes = true, exportSkins = true, exportAnimations = true, exportBlendShape = true,
                boneSize = 10, scaleFactor = 1, fbxVersion = 3, fbxFormat = 0 });
        var exports = ModelExportService.Export(manager, plan.Selected, output, options, Console.WriteLine, CancellationToken.None);
        Check(exports.Count > 0 && exports.All(e => e.Meshes > 0), "real model exported to FBX");
        Check(exports.Sum(e => e.Textures.Length) > 0, "real material textures exported");
        Check(exports.Sum(e => e.Animations.Length) > 0, "real animations included");
        foreach (var export in exports)
        {
            var bytes = File.ReadAllBytes(export.File);
            Check(Encoding.ASCII.GetString(bytes).Contains("AnimationStack"), "FBX contains AnimationStack");
            Console.WriteLine(JsonConvert.SerializeObject(export));
        }
    }
    finally { manager.Clear(); }
    return;
}

if (args.FirstOrDefault() == "--probe-request")
{
    var request = JsonConvert.DeserializeObject<CatalogRequest>(File.ReadAllText(args[2]));
    if (args.Length > 4) request = request with { MeshCatalog = AssetIndexStore.Load(args[4], output, Console.WriteLine, default) };
    Logger.Silent = false; Logger.Flags = LoggerEvent.Warning | LoggerEvent.Error;
    var plan = CabCatalog.Plan(request, default);
    Check(plan.Missing.Length == 0, "request dependencies resolved: " + string.Join(", ", plan.Missing));
    Console.WriteLine($"PLAN {plan.Files.Length} files, {plan.Offsets.Length} offsets");
    var manager = new AssetsManager { Game = GameManager.GetGameByType(request.Game), FilterData = new() { Items = plan.Offsets.ToList() } };
    try
    {
        manager.LoadFiles(plan.Files, mergeSplitAssets: false); manager.FilterData.Items.Clear();
        if (request.MeshCatalog != null)
        {
            var count = SeparateMeshSupport.CompleteLoad(manager, request, plan.Selected, Console.WriteLine, default);
            Console.WriteLine("SEPARATE_MESHES " + count);
        }
        var selected = plan.Selected.Select(a => CabCatalog.Find(manager, a)).ToArray();
        foreach (var clip in selected.OfType<AnimationClip>())
        {
            var acl = clip.m_MuscleClip.m_Clip.m_ACLClip;
            if (!acl.IsSet) continue;
            acl.Process(manager.Game, out var values, out var times);
            Console.WriteLine($"ACL {clip.Name}: kind={acl.GetType().Name}, curves={acl.CurveCount}, values={values.Length}, frames={times.Length}");
            Check(times.Length > 0 && values.LongLength == (long)times.Length * acl.CurveCount, "ACL decoder preserves every curve in every frame");
            Check(values.All(float.IsFinite), "ACL curve values are finite");
        }
        foreach (var file in manager.assetsFileList)
            Console.WriteLine($"CAB {file.fileName} {file.originalPath} @{file.offset}: " + string.Join(", ", file.Objects.GroupBy(o => o.type).Select(g => $"{g.Key}={g.Count()}")));
        foreach (var obj in selected)
        {
            Console.WriteLine($"SELECTED {obj.type} {obj.Name} {obj.m_PathID}");
            if (obj is Animator animator && animator.m_GameObject.TryGet(out var owner))
                Console.WriteLine($"OWNER {owner.Name} transform={owner.m_Transform != null}, HasModel={owner.HasModel()}");
        }
        foreach (var go in manager.assetsFileList.SelectMany(f => f.Objects.OfType<GameObject>()))
        {
            if (go.m_SkinnedMeshRenderer is SkinnedMeshRenderer renderer)
                Console.WriteLine($"RENDERER {go.Name} mesh=({renderer.m_Mesh.m_FileID},{renderer.m_Mesh.m_PathID}) resolved={renderer.m_Mesh.TryGet(out _)}");
        }
        var roots = ModelExportService.ResolveRoots(manager, selected);
        Console.WriteLine("ROOTS " + string.Join(", ", roots.Select(r => r.Name)));
        if (args.Length > 3 && args[3] == "export")
        {
            var options = new ModelExportService.Options(new ModelConverter.Options
                { uvs = Enumerable.Range(0, 8).ToDictionary(i => "UV" + i, i => (i < 2, i < 2 ? i : 0)), texs = new() },
                new Fbx.ExportOptions { exportAllNodes = true, exportSkins = true, exportAnimations = true, exportBlendShape = true,
                    boneSize = 10, scaleFactor = 1, fbxVersion = 3, fbxFormat = 0 });
            var results = ModelExportService.Export(manager, plan.Selected, output, options, Console.WriteLine, default);
            File.WriteAllText(Path.Combine(output, "result.json"), JsonConvert.SerializeObject(results, Formatting.Indented));
            Check(results.Count > 0 && results.All(r => r.Meshes > 0), "requested model exported");
        }
    }
    finally { manager.Clear(); }
    return;
}

if (args.FirstOrDefault() == "--duplicate-copy")
{
    Logger.Silent = true;
    var row = new CatalogAsset { Name = "Monster_Palicus", Type = ClassIDType.Animator,
        Source = args[2], Offset = 1388150, PathID = 541663571111027022 };
    var plan = CabCatalog.Plan(new(GameType.ZZZ, new[] { row }, args[3], ""), default);
    Check(plan.Missing.Length == 0, "noncanonical CAB copy dependencies resolved");
    var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ZZZ),
        FilterData = new() { Items = plan.Offsets.ToList() } };
    try
    {
        manager.LoadFiles(plan.Files, mergeSplitAssets: false);
        Check(CabCatalog.Find(manager, plan.Selected[0]).assetsFile.originalPath.Equals(row.Source, StringComparison.OrdinalIgnoreCase),
            "noncanonical copy loads from selected physical blk");
    }
    finally { manager.Clear(); }
    return;
}

foreach (var decoder in new[] { typeof(ACLLibs.ACL), typeof(ACLLibs.SRACL), typeof(ACLLibs.DBACL) })
{
    System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(decoder.TypeHandle);
    Check(true, "native decoder loads from the current package layout: " + decoder.Name);
}

var fixtures = new[]
{
    new CatalogAsset { Name = "艾莲 \"model\"", Type = ClassIDType.Animator, Container = "assets/a.prefab", Source = @"E:\old\a.blk", Offset = 12, PathID = long.MinValue, Cab = "CAB-a" },
    new CatalogAsset { Name = "Walk", Type = ClassIDType.AnimationClip, Container = "assets/walk.anim", Source = @"E:\old\b.blk", Offset = 30, PathID = 5, Cab = "CAB-b" },
    new CatalogAsset { Name = "model2", Type = ClassIDType.Mesh, Container = "123", Source = @"E:\old\a.blk", Offset = 80, PathID = long.MinValue, Cab = "CAB-c" }
};
foreach (var formatting in new[] { Formatting.None, Formatting.Indented })
{
    var path = Path.Combine(output, formatting + ".json");
    File.WriteAllText(path, JsonConvert.SerializeObject(new { GameType = "ZZZ", AssetEntries = fixtures }, formatting));
    var store = AssetIndexStore.Load(path, Path.Combine(output, "cache"), null, CancellationToken.None);
    Check(store.Count == 3 && store.Entry(0) == fixtures[0], "JSON round trip " + formatting);
    Check(store.Search(new("model", "全部类型", "a.blk", false), default).SequenceEqual(new[] { 0, 2 }), "substring + block filter " + formatting);
    Check(store.Search(new("WALK", "AnimationClip", "", false), default).SequenceEqual(new[] { 1 }), "case insensitive + type filter " + formatting);
    Check(store.Search(new("^model", "Mesh", "", true), default).SequenceEqual(new[] { 2 }), "regex + type filter " + formatting);
    var cached = AssetIndexStore.Load(path, Path.Combine(output, "cache"), null, default);
    Check(cached.Entry(2) == fixtures[2], "binary cache preserves identity " + formatting);
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { store.Search(new("m", "", "", false), cts.Token); throw new Exception("cancel ignored"); }
    catch (OperationCanceledException) { Check(true, "search cancellation"); }
}
Task<DirectoryExportResult> RunDirectory(DirectoryExportPlan plan, string folder,
    Func<PrimaryAssetGroup, string, CancellationToken, Task<PrimaryExportResult>> export,
    Action<string> progress, CancellationToken token, int concurrency = 1)
    => DirectoryExportService.RunAsync(plan, folder, export, progress, token, concurrency, Path.Combine(output, "logs"));

var fixture = new AnimationFbxFixture();
foreach (var preserve in new[] { false, true })
{
    var file = Path.Combine(output, preserve ? "fixed-animation.fbx" : "legacy-animation.fbx");
    Fbx.Exporter.Export(file, fixture, new Fbx.ExportOptions { exportAllNodes = true, exportSkins = true, exportAnimations = true,
        castToBone = true, preserveRootNodeAsNull = preserve, boneSize = 10, scaleFactor = 1, fbxVersion = 3, fbxFormat = 0 });
    var inspected = FbxInspection.Read(file);
    Check(inspected.Roots.Single().Type == (preserve ? "Null" : "LimbNode"), "native FBX root classification with opt-in=" + preserve);
    Check(inspected.Models.Single(m => m.Name == "Bip001").Type == "LimbNode" && inspected.Animations.Contains("Attack"), "root fix preserves skeleton bones and animation stacks");
}
var sourceRoot = Path.Combine(output, "relocated"); Directory.CreateDirectory(sourceRoot);
FbxOptimizationTests.Run(output, Check);
FbxTimeSpanTests.Run(output, Check);
AvatarTests.Run(Check);
var directoryBuilder = new AssetIndexStore.Builder();
foreach (var asset in new[]
{
    new CatalogAsset { Name = "Hero", Type = ClassIDType.Animator, PathID = 1, Source = @"E:\a.blk", Offset = 10, Container = "Assets/Hero/a.fbx" },
    new CatalogAsset { Name = "Walk", Type = ClassIDType.AnimationClip, PathID = 2, Source = @"E:\a.blk", Offset = 10, Container = "Assets/Hero/a.fbx" },
    new CatalogAsset { Name = "Run", Type = ClassIDType.AnimationClip, PathID = 3, Source = @"E:\a.blk", Offset = 10, Container = "Assets/Hero/a.fbx" },
    new CatalogAsset { Name = "Walk", Type = ClassIDType.AnimationClip, PathID = 2, Source = @"E:\copy.blk", Offset = 0, Container = "Assets/Hero/a.fbx" },
    new CatalogAsset { Name = "Walk", Type = ClassIDType.AnimationClip, PathID = 2, Source = @"E:\b.blk", Container = "Assets/Hero/sub/b.fbx" },
    new CatalogAsset { Name = "Outside", Type = ClassIDType.Texture2D, PathID = 1, Source = @"E:\x.blk", Container = "Assets/Heroine/a.png" },
    new CatalogAsset { Name = "Unknown", Type = ClassIDType.Mesh, PathID = 4, Source = @"E:\x.blk", Container = "1234" }
}) directoryBuilder.Add(asset);
var directoryStore = directoryBuilder.Build(GameType.ZZZ, "fixture");
directoryStore.Guessed = new string[directoryStore.Count]; directoryStore.Guessed[^1] = "Assets/Hero/guessed.mesh";
var directoryPlan = directoryStore.CollectDirectory(@"assets\Hero\", true, default);
Check(directoryPlan.Groups.Length == 2 && directoryPlan.DuplicateRows == 1, "directory boundaries, slash normalization and duplicate copies");
Check(directoryPlan.Groups[0].Members.Length == 3 && directoryPlan.Groups[1].Members.Length == 1, "subassets group by parent path; same-name clips in other FBXs stay separate");
Check(directoryStore.CollectDirectory("Assets/Hero", false, default).Groups.Length == 1, "nonrecursive directory collection");
Check(directoryPlan.MatchedRows == 5, "guessed paths do not determine directory membership");
try { ResourcePaths.Normalize("Assets/Hero/../other"); throw new Exception("Traversal accepted"); } catch (ArgumentException) { Check(true, "resource traversal rejected"); }
Check(ResourcePaths.OutputPath(output, "Assets/Hero", "Assets/Hero/A:B.fbx") != ResourcePaths.OutputPath(output, "Assets/Hero", "Assets/Hero/A?B.fbx"), "sanitized filenames cannot collide");
var visits = 0;
var batch = await RunDirectory(directoryPlan, Path.Combine(output, "batch"), (group, folder, token) =>
{
    visits++;
    if (visits == 1) throw new InvalidDataException("fixture failure");
    var file = Path.Combine(folder, "main.fbx"); File.WriteAllText(file, "fixture");
    return Task.FromResult(new PrimaryExportResult([file], 1));
}, null, default);
Check(visits == 2 && batch.Failed == 1 && batch.Succeeded == 1 && batch.Pending == 0, "batch continues after one failed primary resource");
var batchManifest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(batch.ReportPath));
Check((string)batchManifest["State"] == "partial", "batch manifest never reports partial export as complete");
using (var cancelBatch = new CancellationTokenSource())
{
    var progressCount = 0;
    var cancelled = await RunDirectory(directoryPlan, Path.Combine(output, "cancel-batch"), (group, folder, token) =>
    {
        token.ThrowIfCancellationRequested();
        var file = Path.Combine(folder, "main.fbx"); File.WriteAllText(file, "fixture");
        return Task.FromResult(new PrimaryExportResult([file]));
    }, _ => { if (++progressCount == 1) cancelBatch.Cancel(); }, cancelBatch.Token);
    Check(cancelled.Cancelled && cancelled.Succeeded == 1 && cancelled.Pending == 1, "cancellation preserves completed primary exports and records unfinished resources");
}
// Two blocked delegates must both start before either can finish: verifies actual overlap.
var parallelPlan = new DirectoryExportPlan("Assets/Test", true,
    Enumerable.Range(0, 8).Select(i => new PrimaryAssetGroup($"Assets/Test/{i}.asset", [])).ToArray(), 8, 0);
var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int activeExports = 0, peakExports = 0, startedExports = 0;
var parallelBatch = await RunDirectory(parallelPlan, Path.Combine(output, "parallel"), async (group, folder, token) =>
{
    var active = Interlocked.Increment(ref activeExports);
    InterlockedExtensionsMax(active);
    if (Interlocked.Increment(ref startedExports) == 2) bothStarted.TrySetResult();
    try
    {
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(15, token);
        if (group.ResourcePath.EndsWith("3.asset")) throw new IOException("fixture failure");
        var file = Path.Combine(folder, Path.GetFileName(group.ResourcePath) + ".bin"); await File.WriteAllTextAsync(file, "fixture", token);
        return new PrimaryExportResult([file]);
    }
    finally { Interlocked.Decrement(ref activeExports); }
}, null, default, 2);
void InterlockedExtensionsMax(int value)
{
    int before;
    do { before = peakExports; if (before >= value) return; }
    while (Interlocked.CompareExchange(ref peakExports, value, before) != before);
}
Check(peakExports == 2 && parallelBatch.Succeeded == 7 && parallelBatch.Failed == 1, "bounded parallel exports overlap and continue after failures");
Check(File.ReadAllLines(Path.ChangeExtension(parallelBatch.ReportPath, ".jsonl")).Length == 8, "parallel journal has exactly one result per primary resource");
using (var cancelParallel = new CancellationTokenSource())
{
    int running = 0;
    var cancelled = await RunDirectory(parallelPlan, Path.Combine(output, "cancel-parallel"), async (group, folder, token) =>
    {
        if (Interlocked.Increment(ref running) == 2) cancelParallel.Cancel();
        await Task.Delay(Timeout.Infinite, token);
        return new PrimaryExportResult([]);
    }, null, cancelParallel.Token, 2);
    Check(cancelled.Cancelled && cancelled.Pending == 8 && cancelled.Succeeded == 0 && running == 2,
        "cancellation stops all active exports and does not start queued items");
}
var selectionPlan = new DirectoryExportPlan("Assets/Hero", true, [
    new("Assets/Hero/Info.json", []), new("Assets/Hero/Ani/Idle.fbx", directoryPlan.Groups[0].Members),
    new("Assets/Hero/Ani/Combat/Attack.fbx", []), new("Assets/Hero/Textures/Body.png", [])], 4, 0);
var selectedPlan = selectionPlan.Select(["Assets/Hero", "assets/hero/ani", "Assets/Hero/Textures"], [".FBX", ".png"]);
Check(selectedPlan.Groups.Select(g => g.ResourcePath).SequenceEqual(new[] { "Assets/Hero/Ani/Idle.fbx", "Assets/Hero/Textures/Body.png" }), "directory and extension multiselect excludes unchecked descendants");
Check(selectedPlan.Groups[0].Members.Length == 3, "file filtering retains every subasset of a selected FBX");
Check(selectionPlan.Select([], [".fbx"]).Groups.Length == 0 && selectionPlan.Select(["Assets/Hero"], []).Groups.Length == 0, "empty selection never means export all");
Check(selectionPlan.Within("Assets/Hero/Ani").Select(g => g.ResourcePath).SequenceEqual(new[] { "Assets/Hero/Ani/Idle.fbx", "Assets/Hero/Ani/Combat/Attack.fbx" }), "preview scope includes the highlighted folder and its descendants");
Check(selectionPlan.Within("Assets/Hero").Length == 4 && selectionPlan.Within("Assets/Hero/Ani/Combat").Single().ResourcePath == "Assets/Hero/Ani/Combat/Attack.fbx", "preview scope never leaks sibling folders");
Check(ResourcePaths.OutputPath(output, "Assets/Hero", "Assets/Hero/Ani/Idle.fbx") == Path.Combine(output, "Ani", "Idle.fbx"), "source directory layout has no batch or per-file wrapper");
var longName = new string('a', 160) + ".fbx";
Check(ResourcePaths.OutputSegment(longName) == longName, "valid long source names are preserved");

var sharedPlan = new DirectoryExportPlan("Assets/Hero", true,
    [new("Assets/Hero/Ani/Idle.fbx", []), new("Assets/Hero/Ani/Walk.fbx", [])], 2, 0);
Task<PrimaryExportResult> ExportShared(PrimaryAssetGroup group, string folder, CancellationToken token)
{
    var file = Path.Combine(folder, Path.GetFileName(group.ResourcePath)); File.WriteAllText(file, group.ResourcePath);
    File.WriteAllText(Path.Combine(folder, "shared.png"), "texture");
    return Task.FromResult(new PrimaryExportResult([file]));
}
var shared = await RunDirectory(sharedPlan, Path.Combine(output, "shared"), ExportShared, null, default, 2);
Check(shared.Succeeded == 2 && File.Exists(Path.Combine(shared.OutputDirectory, "Ani", "Idle.fbx")) &&
    Directory.GetFiles(shared.OutputDirectory, "*", SearchOption.AllDirectories).Length == 3, "parallel resources share identical dependencies without adding directories or metadata");
Check(!shared.ReportPath.StartsWith(shared.OutputDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "export journal stays outside output assets");
var existing = await RunDirectory(sharedPlan, shared.OutputDirectory, ExportShared, null, default, 2);
Check(existing.Skipped == 2 && existing.Failed == 0 && existing.Pending == 0 && File.ReadAllText(Path.Combine(shared.OutputDirectory, "Ani", "Idle.fbx")) == sharedPlan.Groups[0].ResourcePath, "existing files are skipped without overwrite or duplicate suffixes");
var conflict = await RunDirectory(sharedPlan, Path.Combine(output, "conflict"), (group, folder, token) =>
{
    var result = ExportShared(group, folder, token);
    File.WriteAllText(Path.Combine(folder, "shared.png"), group.ResourcePath);
    return result;
}, null, default, 2);
Check(conflict.Succeeded == 1 && conflict.Failed == 1 && Directory.GetFiles(conflict.OutputDirectory, "*.fbx", SearchOption.AllDirectories).Length == 1,
    "conflicting dependencies cannot overwrite another resource or publish a partial model");
var skipped = await RunDirectory(sharedPlan, Path.Combine(output, "skipped"), (_, _, _) => Task.FromResult(PrimaryExportResult.Skip("unsupported fixture")), null, default, 2);
Check(skipped.Skipped == 2 && skipped.Failed == 0 && skipped.Pending == 0 && !Directory.Exists(skipped.OutputDirectory), "unsupported resources only produce a log, no substitute JSON or empty output tree");
var broken = await RunDirectory(sharedPlan, Path.Combine(output, "broken"), (group, folder, token) =>
{
    File.WriteAllText(Path.Combine(folder, "partial.fbx"), "partial"); throw new IOException("fixture export failure");
}, null, default, 2);
Check(broken.Failed == 2 && !Directory.Exists(broken.OutputDirectory), "failed exports leave no incomplete output files");
var meshPath = "Assets/OriginalResRepos/ART/DiscreteMeshAssets/Hero_Origin_Model/Body.mesh";
var otherMeshPath = "Assets/OriginalResRepos/ART/DiscreteMeshAssets/Hero_Other_Model/Body.mesh";
var meshBuilder = new AssetIndexStore.Builder();
meshBuilder.Add(new CatalogAsset { Name = "Body", Type = ClassIDType.Mesh, Source = @"E:\old\mesh-a.blk", PathID = 1, Container = meshPath });
meshBuilder.Add(new CatalogAsset { Name = "Body", Type = ClassIDType.Mesh, Source = @"E:\old\mesh-b.blk", PathID = 2, Container = otherMeshPath });
meshBuilder.Add(new CatalogAsset { Name = "Body_LOD1", Type = ClassIDType.Mesh, Source = @"E:\old\mesh-c.blk", PathID = 3, Container = meshPath.Replace("Body.mesh", "Body_LOD1.mesh") });
meshBuilder.Add(new CatalogAsset { Name = "Body", Type = ClassIDType.Texture2D, Source = @"E:\old\texture.blk", PathID = 4, Container = meshPath });
var meshStore = meshBuilder.Build(GameType.ZZZ, "test");
Check(meshStore.FindMeshes(new[] { meshPath }, default)[meshPath].Single().PathID == 1, "separate meshes match exact model path, not same-name skin, LOD or other asset type");
meshStore.Container[0] = PathRecovery.HashOf(meshPath).ToString();
Check(meshStore.FindMeshes(new[] { meshPath }, default)[meshPath].Single().PathID == 1, "separate mesh lookup validates unresolved container hash");
var missingMesh = meshPath.Replace("Body.mesh", "Missing.mesh");
Check(meshStore.FindMeshes(new[] { missingMesh }, default)[missingMesh].Length == 0, "missing separate mesh is not replaced by a name guess");
var tsvPath = Path.Combine(output, "fixture.tsv");
File.WriteAllText(tsvPath, "Name\tType\tBlk\tOffset\tPathID\tContainer\tHash\tSource\nWalk\tAnimationClip\tb.blk\t30\t5\tassets/walk.anim\t\tE:\\old\\b.blk\n");
Check(AssetIndexStore.Load(tsvPath, Path.Combine(output, "cache"), null, default).Entry(0).Name == "Walk", "TSV import");
var messagePackPath = Path.Combine(output, "fixture.map");
using (var stream = File.Create(messagePackPath))
    MessagePack.MessagePackSerializer.Serialize(stream, new AssetMap { GameType = GameType.ZZZ, AssetEntries = fixtures.Select(a => new AssetEntry
        { Name = a.Name, Type = a.Type, Container = a.Container, Source = a.Source, PathID = a.PathID, Offset = a.Offset }).ToList() },
        MessagePack.MessagePackSerializerOptions.Standard.WithCompression(MessagePack.MessagePackCompression.Lz4BlockArray));
var importedMap = AssetIndexStore.Load(messagePackPath, Path.Combine(output, "cache"), null, default);
Check(importedMap.Count == fixtures.Length && importedMap.Entry(0).PathID == long.MinValue, "legacy MessagePack import");
foreach (var name in new[] { "a.blk", "b.blk", "c.blk" }) File.WriteAllText(Path.Combine(sourceRoot, name), "test");
var mapPath = Path.Combine(output, "test.bin");
CabCatalog.Write(mapPath, @"E:\old", new[]
{
    new CabEntry("CAB-a", @"E:\old\a.blk", 12, new[] { "CAB-b" }),
    new CabEntry("CAB-b", @"E:\old\b.blk", 30, new[] { "CAB-a", "CAB-c" }),
    new CabEntry("CAB-c", @"E:\old\a.blk", 80, Array.Empty<string>())
});
var duplicateMap = Path.Combine(output, "duplicate.bin");
CabCatalog.Write(duplicateMap, @"E:\old", new[] { new CabEntry("CAB-a", @"E:\old\a.blk", 12, Array.Empty<string>()),
    new CabEntry("CAB-a", @"E:\old\c.blk", 100, Array.Empty<string>()) });
Check(CabCatalog.Read(duplicateMap, default).Entries.Count == 1, "generated CABMap keeps one canonical location per CAB");
var load = CabCatalog.Plan(new(GameType.ZZZ, new[] { fixtures[0] }, mapPath, sourceRoot), default);
Check(load.Files.Length == 2 && load.Offsets.Length == 3 && load.Missing.Length == 0, "transitive dependencies with cycle and same-blk offsets");
Check(load.Selected[0].Source == Path.Combine(sourceRoot, "a.blk"), "source root relocation");
File.Delete(Path.Combine(sourceRoot, "b.blk"));
Check(CabCatalog.Plan(new(GameType.ZZZ, new[] { fixtures[0] }, mapPath, sourceRoot), default).Missing.Length == 1, "missing dependency is reported");
var recovery = new PathRecovery.Request { Name = new[] { "Hero", "seed" }, Type = new[] { "Material", "Material" },
    Container = new[] { PathRecovery.HashOf("assets/hero.mat").ToString(), "assets/seed.mat" }, Blk = new[] { "a.blk", "a.blk" }, Count = 2,
    KnownPaths = new Dictionary<ulong, string> { [1] = "assets/seed.mat" } };
var recovered = PathRecovery.Recover(recovery, null, default);
Check(recovered.SolvedHashes == 1 && PathRecovery.HashOf(recovery.Container[0]) == PathRecovery.HashOf("assets/hero.mat"), "path recovery validates hash");
Console.WriteLine("ALL REGRESSION CHECKS PASSED");
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }

sealed class PreviewBridge : IStudioBridge
{
    public Task LoadAsync(CatalogRequest request, CancellationToken token) => Task.CompletedTask;
    public Task<string> ExportAssetsAsync(CatalogRequest request, string folder, CancellationToken token) => Task.FromResult("");
    public Task<string> ExportModelsAsync(CatalogRequest request, string folder, CancellationToken token) => Task.FromResult("");
    public Task<DirectoryExportResult> ExportDirectoryAsync(DirectoryExportPlan plan, CatalogRequest context, string folder, Action<string> report, CancellationToken token)
        => Task.FromResult(new DirectoryExportResult(folder, 0, 0, plan.Groups.Length, false));
}
