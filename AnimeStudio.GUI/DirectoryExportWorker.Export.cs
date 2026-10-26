using System;
using System.IO;
using System.Linq;
using System.Threading;
using AnimeStudio.AssetExplorer;
namespace AnimeStudio.GUI;
internal static partial class DirectoryExportWorker
{
    private static PrimaryExportResult Export(CatalogRequest context, PrimaryAssetGroup group, string output,
        ModelExportService.Options options, CabCatalog cachedMap, CancellationToken cancellation)
    {
        Action<string> report = null;
        var request = context with { Assets = group.Members };
        if (group.IsFbx && string.IsNullOrWhiteSpace(request.CabMap)) throw new InvalidOperationException("FBX 目录导出需要 CABMap。");
        var load = CabCatalog.Plan(request, cancellation, cachedMap);
        if (load.Missing.Length > 0) throw new FileNotFoundException(string.Join(Environment.NewLine, load.Missing.Take(8)));
        if (group.IsCompiledController && request.MeshCatalog is IControllerContextCatalog catalog)
        {
            var rows = catalog.FindControllerContext(group.ResourcePath, cancellation);
            if (rows.Length > 0)
            {
                var contextLoad = ControllerContextLoader.Plan(request, rows, cachedMap, cancellation);
                if (contextLoad.Missing.Length == 0)
                    load = load with { Files = load.Files.Concat(contextLoad.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        Offsets = load.Offsets.Concat(contextLoad.Offsets).DistinctBy(o => (o.Source.ToUpperInvariant(), o.Offset)).ToArray() };
            }
        }
        var manager = new AssetsManager { Game = GameManager.GetGameByType(request.Game), ResolveDependencies = false,
            FilterData = new() { Items = load.Offsets.ToList() } };
        try
        {
            using (cancellation.Register(() => manager.tokenSource.Cancel())) manager.LoadFiles(load.Files, mergeSplitAssets: false);
            cancellation.ThrowIfCancellationRequested(); manager.FilterData.Items.Clear();
            if (group.IsCompiledController)
                return EditableControllerExporter.Export(manager, group, load.Selected, output, cancellation);
            var content = PrimaryAssetResolver.Resolve(manager, group, load.Selected);
            if (group.IsFbx)
            {
                // Animation-only FBXs can contain placeholder renderers but no imported meshes/Animator.
                var animationOnly = content.Clips.Length > 0 && !content.Members.Any(o => o is Mesh or Animator);
                if (!animationOnly)
                {
                    var withRoots = load.Selected.Concat(content.Roots.Select(PrimaryAssetResolver.Reference)).Distinct().ToArray();
                    SeparateMeshSupport.CompleteLoad(manager, request, withRoots, report, cancellation, cachedMap);
                    content = PrimaryAssetResolver.Resolve(manager, group, load.Selected);
                }
                return PrimaryFbxExporter.Export(manager, group, content, output, options, cancellation);
            }
            return MainForm.ExplorerBridge.ExportPrimaryObject(group, content, output, cancellation);
        }
        finally { manager.Clear(); }
    }
}
