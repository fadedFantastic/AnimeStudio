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
var sourceRoot = Path.Combine(output, "relocated"); Directory.CreateDirectory(sourceRoot);
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
}
