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
