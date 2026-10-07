namespace AnimeStudio.AssetExplorer;

public sealed record PrimaryAssetContent(AnimeStudio.Object Main, AnimeStudio.Object[] Members, GameObject[] Roots, AnimationClip[] Clips);

public static class PrimaryAssetResolver
{
    public static PrimaryAssetContent Resolve(AssetsManager manager, PrimaryAssetGroup group, CatalogAsset[] selected)
    {
        var members = selected.Select(a => CabCatalog.Find(manager, a)).ToHashSet();
        var files = members.Select(o => o.assetsFile).ToHashSet();
        var main = new List<AnimeStudio.Object>();
        var bundlePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in files.SelectMany(f => f.Objects.OfType<AssetBundle>()))
            foreach (var entry in bundle.m_Container)
            {
                bundlePaths.Add(entry.Key);
                if (!Matches(entry.Key, group.ResourcePath)) continue;
                if (entry.Value.asset.TryGet(out var primary)) { members.Add(primary); main.Add(primary); }
                var start = Math.Max(0, entry.Value.preloadIndex);
                var end = Math.Min((long)entry.Value.preloadIndex + entry.Value.preloadSize, bundle.m_PreloadTable.Count);
                for (var i = start; i < end; i++)
                    if (bundle.m_PreloadTable[i].TryGet(out var item) && files.Contains(item.assetsFile)) members.Add(item);
            }

        var roots = new HashSet<GameObject>();
        void Add(AnimeStudio.Object obj)
        {
            var go = obj as GameObject;
            if (obj is Component component) component.m_GameObject.TryGet(out go);
            if (go?.m_Transform == null) return;
            var seen = new HashSet<GameObject>();
            while (seen.Add(go) && go.m_Transform.m_Father.TryGet(out var parent) && parent.m_GameObject.TryGet(out var parentGo))
            {
                if (!files.Contains(parentGo.assetsFile)) break;
                go = parentGo;
            }
            roots.Add(go);
        }
        foreach (var obj in main) Add(obj);
        if (roots.Count == 0) foreach (var obj in members) Add(obj);
        if (roots.Count == 0 && group.IsFbx)
        {
            var allRoots = files.SelectMany(f => f.Objects.OfType<GameObject>())
                .Where(go => go.m_Transform != null && !go.m_Transform.m_Father.TryGet(out _)).ToArray();
            var stem = Path.GetFileNameWithoutExtension(group.ResourcePath);
            var named = allRoots.Where(go => go.Name.Equals(stem, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (named.Length > 0) foreach (var go in named) roots.Add(go);
            else if (bundlePaths.Count <= 1) foreach (var go in allRoots) roots.Add(go);
            else throw new InvalidDataException("同一 CAB 有多个主资源，无法确认该 FBX 的骨架归属：" + group.ResourcePath);
        }
        var ordered = members.OrderBy(o => o.m_PathID).ToArray();
        return new(main.FirstOrDefault() ?? ordered.FirstOrDefault(), ordered,
            roots.OrderBy(go => go.Name, StringComparer.Ordinal).ThenBy(go => go.m_PathID).ToArray(),
            ordered.OfType<AnimationClip>().ToArray());
    }

    public static CatalogAsset Reference(AnimeStudio.Object obj) => new()
    {
        Name = obj.Name, Type = obj.type, Source = obj.assetsFile.originalPath ?? obj.assetsFile.fullName,
        Offset = obj.assetsFile.offset, Cab = obj.assetsFile.fileName, PathID = obj.m_PathID
    };

    private static bool Matches(string value, string resource)
    {
        if (value.Replace('\\', '/').Equals(resource, StringComparison.OrdinalIgnoreCase)) return true;
        return ulong.TryParse(value, out var hash) && hash == PathRecovery.HashOf(resource);
    }
}
