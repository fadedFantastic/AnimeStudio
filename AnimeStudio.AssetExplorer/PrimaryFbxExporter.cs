namespace AnimeStudio.AssetExplorer;

public static class PrimaryFbxExporter
{
    public static PrimaryExportResult Export(AssetsManager manager, PrimaryAssetGroup group, PrimaryAssetContent content,
        string folder, ModelExportService.Options options, CancellationToken token)
    {
        if (content.Roots.Length == 0) throw new InvalidDataException("该 FBX 没有可恢复的模型或骨架层级：" + group.ResourcePath);
        var clips = content.Clips;
        var conversion = options.Conversion with { game = manager.Game, collectAnimations = false, imageFormat = ImageFormat.Png,
            exportMaterials = true, materials = new HashSet<Material>() };
        var name = Path.GetFileNameWithoutExtension(group.ResourcePath);
        var model = content.Roots.Length == 1
            ? new ModelConverter(content.Roots[0], conversion, clips)
            : new ModelConverter(name, content.Roots.ToList(), conversion, clips);

        // ModelConverter also collects legacy Animation components; keep only this primary file's clips.
        var expected = clips.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        model.AnimationList.RemoveAll(a => !expected.ContainsKey(a.Name));
        foreach (var pair in expected)
        {
            var animations = model.AnimationList.Where(a => a.Name == pair.Key).ToArray();
            if (animations.Length != pair.Value || animations.Any(a => !a.TrackList.Any(t => !string.IsNullOrEmpty(t.Path))))
                throw new InvalidDataException("子动画未能完整绑定到主 FBX 的骨架：" + pair.Key);
        }
        if (model.MeshList.Count == 0 && clips.Length == 0) throw new InvalidDataException("该 FBX 没有网格或动画。");
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, Path.ChangeExtension(ResourcePaths.OutputSegment(Path.GetFileName(group.ResourcePath)), ".fbx"));
        ExportNative(file, model, options.Fbx with { exportAllNodes = true, exportAnimations = true,
            exportSkins = true, preserveRootNodeAsNull = true, optimizeAnimationSize = true,
            castToBone = model.MeshList.Count == 0 || options.Fbx.castToBone }, token);

        // Native FBX materials and their referenced textures are already exported together.
        // Do not emit per-resource reports or unrelated CAB dependency dumps here.
        return new PrimaryExportResult([file], model.AnimationList.Count);
    }

    private static void ExportNative(string destination, IImported model, Fbx.ExportOptions options, CancellationToken token)
    {
        // The native FBX SDK can silently fail on long absolute output paths.
        // Export in a short owned directory, then use .NET's long-path-aware file APIs.
        var staging = Directory.CreateTempSubdirectory("aex-");
        var cwd = Environment.CurrentDirectory;
        try
        {
            var stagedFile = Path.Combine(staging.FullName, "resource.fbx");
            ModelExporter.ExportFbx(stagedFile, model, options);
            if (!File.Exists(stagedFile) || new FileInfo(stagedFile).Length == 0) throw new IOException("FBX 导出器未生成有效文件。");
            foreach (var source in Directory.EnumerateFiles(staging.FullName, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                var target = source == stagedFile ? destination : Path.Combine(Path.GetDirectoryName(destination), Path.GetRelativePath(staging.FullName, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Move(source, target);
            }
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(staging.FullName).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) staging.Delete(true);
        }
    }
}
