using System.Text;
using System.Text.RegularExpressions;

namespace AnimeStudio.AssetExplorer;

/// <summary>ZZZ strips renderer mesh pointers and loads DiscreteMeshAssets at runtime.</summary>
public static class SeparateMeshSupport
{
    internal sealed record Binding(CatalogAsset Component, string Path);

    public static int CompleteLoad(AssetsManager manager, CatalogRequest request, CatalogAsset[] selected,
        Action<string> report, CancellationToken token)
    {
        if (!request.Game.IsZZZGroup()) return 0;
        var bindings = FindBindings(manager, selected, token);
        if (bindings.Count == 0) return 0;
        if (request.MeshCatalog == null)
            throw new InvalidOperationException($"模型包含 {bindings.Count} 个分离网格，需要从 Asset Explorer 的清单中补齐。请打开完整资源清单后重试。");
        report?.Invoke($"查找 {bindings.Count} 个分离网格…");
        var found = request.MeshCatalog.FindMeshes(bindings.SelectMany(CandidatePaths), token);
        var chosen = new Dictionary<string, CatalogAsset>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var binding in bindings)
        {
            if (chosen.ContainsKey(binding.Path)) continue;
            var matches = CandidatePaths(binding).Select(path => found.GetValueOrDefault(path) ?? [])
                .FirstOrDefault(rows => rows.Length > 0) ?? [];
            if (matches.Length == 0) { missing.Add(binding.Path); continue; }
            // Multiple physical copies are OK; different objects under one path require a fresh index.
            if (matches.Select(a => (a.Name, a.PathID)).Distinct().Count() != 1)
                throw new InvalidDataException("分离网格路径存在多个不同对象，无法安全选择，请更新清单：" + binding.Path);
            chosen.Add(binding.Path, matches.OrderBy(a => a.Source, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Offset).First());
        }
        if (missing.Count > 0)
            throw new InvalidDataException($"模型骨架已加载，但清单缺少 {missing.Count} 个分离网格，请更新或打开完整清单：\n" + string.Join("\n", missing.Take(6)));
        var extras = chosen.Values.Distinct().ToArray();
        var combined = CabCatalog.Plan(request with { Assets = request.Assets.Concat(extras).ToArray() }, token);
        if (combined.Missing.Length > 0) throw new FileNotFoundException(string.Join(Environment.NewLine, combined.Missing.Take(12)));
        token.ThrowIfCancellationRequested();
        // ReadAssets processes the entire file list, so reload once instead of appending duplicate objects.
        manager.Clear();
        manager.FilterData = new() { Items = combined.Offsets.ToList() };
        try
        {
            using (token.Register(() => manager.tokenSource.Cancel())) manager.LoadFilesPreprocessed(combined.Files);
            token.ThrowIfCancellationRequested();
            var resolved = extras.Select((asset, i) => (asset, combined.Selected[request.Assets.Length + i])).ToDictionary(x => x.asset, x => x.Item2);
            foreach (var binding in bindings)
            {
                token.ThrowIfCancellationRequested();
                var component = CabCatalog.Find(manager, binding.Component);
                var mesh = (Mesh)CabCatalog.Find(manager, resolved[chosen[binding.Path]]);
                MeshPointer(component).Set(mesh);
            }
            foreach (var asset in selected) CabCatalog.Find(manager, asset);
        }
        finally { manager.FilterData = new() { Items = [] }; }
        report?.Invoke($"已补齐 {bindings.Count} 个分离网格。");
        return bindings.Count;
    }

    internal static List<Binding> FindBindings(AssetsManager manager, CatalogAsset[] selected, CancellationToken token)
    {
        var roots = new HashSet<GameObject>();
        foreach (var asset in selected)
        {
            var obj = CabCatalog.Find(manager, asset);
            GameObject go = obj as GameObject;
            if (obj is Component component) component.m_GameObject.TryGet(out go);
            if (go == null) continue;
            var seen = new HashSet<GameObject>();
            while (seen.Add(go) && go.m_Transform?.m_Father.TryGet(out var parent) == true && parent.m_GameObject.TryGet(out var parentGo)) go = parentGo;
            roots.Add(go);
        }
        var result = new List<Binding>();
        var visited = new HashSet<GameObject>();
        var work = new Stack<(GameObject Go, string Model)>();
        foreach (var root in roots) work.Push((root, root.Name));
        while (work.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            var go = current.Go;
            if (!visited.Add(go)) continue;
            var model = go.Name.EndsWith("_Model", StringComparison.OrdinalIgnoreCase) ? go.Name : current.Model;
            foreach (var component in new Component[] { go.m_SkinnedMeshRenderer, go.m_MeshFilter }.Where(c => c != null))
            {
                if (!MeshPointer(component).IsNull) continue;
                var explicitPath = ReadLodPath(go);
                var path = explicitPath ?? $"Assets/OriginalResRepos/ART/DiscreteMeshAssets/{model}/{go.Name}.mesh";
                result.Add(new Binding(new CatalogAsset { Name = go.Name, Type = component.type, PathID = component.m_PathID,
                    Source = component.assetsFile.originalPath ?? component.assetsFile.fullName,
                    Offset = component.assetsFile.offset, Cab = component.assetsFile.fileName }, path));
            }
            if (go.m_Transform != null)
                foreach (var ptr in go.m_Transform.m_Children)
                    if (ptr.TryGet(out var child) && child.m_GameObject.TryGet(out var childGo)) work.Push((childGo, model));
        }
        return result;
    }

    private static PPtr<Mesh> MeshPointer(AnimeStudio.Object component) => component switch
    {
        SkinnedMeshRenderer renderer => renderer.m_Mesh,
        MeshFilter filter => filter.m_Mesh,
        _ => throw new InvalidOperationException("不是网格组件。")
    };

    private static IEnumerable<string> CandidatePaths(Binding binding)
    {
        yield return binding.Path;
        var slash = binding.Path.LastIndexOf('/');
        // Older NapLodController strings use a SeparateMesh_<model>_ prefix,
        // while newer catalogs store the same mesh under its component name.
        if (slash >= 0 && binding.Path[(slash + 1)..].StartsWith("SeparateMesh_", StringComparison.OrdinalIgnoreCase))
            yield return binding.Path[..(slash + 1)] + binding.Component.Name + ".mesh";
    }

    private static string ReadLodPath(GameObject go)
    {
        foreach (var ptr in go.m_Components)
        {
            if (!ptr.TryGet<MonoBehaviour>(out var behaviour) || behaviour.Name != "NapLodController") continue;
            var text = Encoding.UTF8.GetString(behaviour.GetRawData());
            var paths = Regex.Matches(text, @"Assets[/\\][^\x00\r\n]{1,2048}?\.mesh", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))
                .Select(m => m.Value.Replace('\\', '/')).ToArray();
            if (paths.Length > 0) return paths.FirstOrDefault(p => p.EndsWith("/" + go.Name + ".mesh", StringComparison.OrdinalIgnoreCase)) ?? paths[0];
        }
        return null;
    }
}
