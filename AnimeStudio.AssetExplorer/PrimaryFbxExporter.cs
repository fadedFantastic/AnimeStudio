using Newtonsoft.Json;

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
        var file = Path.Combine(folder, ResourcePaths.SafeSegment(name) + ".fbx");
        ExportNative(file, model, options.Fbx with { exportAllNodes = true, exportAnimations = true,
            exportSkins = true, castToBone = model.MeshList.Count == 0 || options.Fbx.castToBone }, token);

        // Keep dynamic material dependencies alongside the main file; subanimations stay inside the FBX.
        var materials = manager.assetsFileList.SelectMany(f => f.Objects.OfType<Material>()).ToArray();
        if (materials.Length > 0)
        {
            var dir = Path.Combine(folder, "Materials"); Directory.CreateDirectory(dir);
            foreach (var material in materials)
            {
                token.ThrowIfCancellationRequested();
                File.WriteAllText(Path.Combine(dir, ResourcePaths.SafeSegment(material.Name) + "-" + material.m_PathID + ".json"),
                    JsonConvert.SerializeObject((object)material.ToType() ?? material, Formatting.Indented));
            }
        }
        foreach (var texture in manager.assetsFileList.SelectMany(f => f.Objects.OfType<Texture2D>()))
        {
            token.ThrowIfCancellationRequested();
            var dir = Path.Combine(folder, "Textures"); Directory.CreateDirectory(dir);
            using var image = texture.ConvertToStream(ImageFormat.Png, true);
            if (image == null) throw new InvalidDataException("贴图转换失败：" + texture.Name);
            var identity = texture.assetsFile.fileName + "-" + texture.m_PathID;
            var path = Path.Combine(dir, ResourcePaths.SafeSegment(texture.Name) + "-" + identity + ".png");
            using var output = File.Create(path); image.Position = 0; image.CopyTo(output);
        }
        File.WriteAllText(Path.Combine(folder, "primary-resource.json"), JsonConvert.SerializeObject(new
        {
            group.ResourcePath, MainFile = Path.GetFileName(file), Meshes = model.MeshList.Count,
            SubAssets = group.Members, Animations = model.AnimationList.Select(a => a.Name).ToArray()
        }, Formatting.Indented));
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
