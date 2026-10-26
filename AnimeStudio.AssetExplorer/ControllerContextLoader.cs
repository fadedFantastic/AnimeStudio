namespace AnimeStudio.AssetExplorer;

public static class ControllerContextLoader
{
    public static LoadPlan Plan(CatalogRequest request, CatalogAsset[] roots, CabCatalog map, CancellationToken token)
    {
        var context = CabCatalog.Plan(request with { Assets = roots }, token, map, includeDependencies: false);
        if (context.Missing.Length > 0 || map == null) return context;
        // Only add the Animator's Avatar CAB. Loading a prefab's entire dependency
        // graph would also load unrelated meshes, materials and textures.
        var headers = new AssetsManager { Game = GameManager.GetGameByType(request.Game), SkipProcess = true, ResolveDependencies = false,
            FilterData = new() { Items = context.Offsets.ToList() } };
        var avatars = new List<CatalogAsset>();
        try
        {
            using (token.Register(() => headers.tokenSource.Cancel())) headers.LoadFiles(context.Files, mergeSplitAssets: false);
            token.ThrowIfCancellationRequested();
            foreach (var row in context.Selected)
                foreach (var file in headers.assetsFileList.Where(f =>
                    Path.GetFullPath(f.originalPath ?? f.fullName).Equals(Path.GetFullPath(row.Source), StringComparison.OrdinalIgnoreCase) &&
                    (row.Offset < 0 || f.offset == row.Offset) && (string.IsNullOrEmpty(row.Cab) || f.fileName.Equals(row.Cab, StringComparison.OrdinalIgnoreCase))))
                    foreach (var info in file.m_Objects.Where(o => o.classID == (int)ClassIDType.Animator && o.m_PathID == row.PathID))
                    {
                        token.ThrowIfCancellationRequested();
                        var animator = new Animator(new ObjectReader(file.reader, file, info, headers.Game));
                        var pointer = animator.m_Avatar;
                        if (pointer.m_PathID == 0 || pointer.m_FileID == 0) continue;
                        if (pointer.m_FileID < 1 || pointer.m_FileID > file.m_Externals.Count) continue;
                        var name = file.m_Externals[pointer.m_FileID - 1].fileName;
                        var cab = map.FindCab(name).FirstOrDefault();
                        if (cab != null) avatars.Add(new() { Name = "Avatar", Type = ClassIDType.Avatar, Cab = name,
                            PathID = pointer.m_PathID, Source = cab.Source, Offset = cab.Offset });
                    }
        }
        finally { headers.Clear(); }
        if (avatars.Count == 0) return context;
        var avatarPlan = CabCatalog.Plan(request with { Assets = avatars.Distinct().ToArray() }, token, map, includeDependencies: false);
        return context with { Files = context.Files.Concat(avatarPlan.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Offsets = context.Offsets.Concat(avatarPlan.Offsets).DistinctBy(o => (o.Source.ToUpperInvariant(), o.Offset)).ToArray(),
            Missing = context.Missing.Concat(avatarPlan.Missing).ToArray() };
    }
}
