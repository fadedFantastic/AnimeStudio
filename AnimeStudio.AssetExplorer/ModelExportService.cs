using Newtonsoft.Json;

namespace AnimeStudio.AssetExplorer;

public static class ModelExportService
{
    public sealed record Options(ModelConverter.Options Conversion, Fbx.ExportOptions Fbx);
    public sealed record ExportedModel(string File, string[] Animations, string[] Textures, int Meshes);

    public static IReadOnlyList<ExportedModel> Export(AssetsManager manager, CatalogAsset[] selected,
        string destination, Options options, Action<string> report, CancellationToken token)
    {
        var objects = selected.Select(a => CabCatalog.Find(manager, a)).ToArray();
        var explicitClips = objects.OfType<AnimationClip>().ToArray();
        var roots = ResolveRoots(manager, objects);
        if (roots.Count == 0) throw new InvalidOperationException("选中的资源没有可导出的模型层级。请选择 Animator、GameObject 或模型 Mesh；动画可与模型一起加入导出列表。");
        var outputs = new List<ExportedModel>();
        var exportRoot = Path.GetFullPath(destination);
        Directory.CreateDirectory(exportRoot);
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            report?.Invoke("收集模型、动画和贴图：" + root.m_Name);
            var hierarchy = Hierarchy(root).ToArray();
            var files = hierarchy.Select(go => go.assetsFile).ToHashSet();
            var clips = files.SelectMany(f => f.Objects.OfType<AnimationClip>()).ToHashSet();
            clips.UnionWith(explicitClips);
            foreach (var go in hierarchy)
            {
                if (go.m_Animation != null)
                    foreach (var pointer in go.m_Animation.m_Animations) if (pointer.TryGet(out var clip)) clips.Add(clip);
                if (go.m_Animator?.m_Controller.TryGet(out var controller) == true) CollectController(controller, clips);
                foreach (var renderer in new Renderer[] { go.m_MeshRenderer, go.m_SkinnedMeshRenderer }.Where(x => x != null))
                    foreach (var materialPtr in renderer.m_Materials)
                    {
                        if (materialPtr.m_PathID == 0) continue;
                        if (!materialPtr.TryGet(out var material)) throw new InvalidDataException($"{go.m_Name} 缺少材质依赖，请更新 CABMap。");
                        foreach (var texture in material.m_SavedProperties.m_TexEnvs)
                            if (texture.Value.m_Texture.m_PathID != 0 && !texture.Value.m_Texture.TryGet(out _))
                                throw new InvalidDataException($"材质 {material.m_Name} 缺少贴图 {texture.Key}，请更新 CABMap。");
                    }
            }
            var conversion = options.Conversion with { game = manager.Game, collectAnimations = true,
                imageFormat = ImageFormat.Png, exportMaterials = true, materials = new HashSet<Material>() };
            var model = new ModelConverter(root, conversion, clips.ToArray());
            if (model.MeshList.Count == 0) throw new InvalidDataException(root.m_Name + " 没有可转换的网格。");
            var convertedNames = model.AnimationList.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            var missing = clips.Where(c => !convertedNames.Contains(c.m_Name)).ToArray();
            if (missing.Length > 0) throw new InvalidDataException("以下动画未能转换，未将本模型标记为成功：" + string.Join(", ", missing.Select(c => c.m_Name)));
            if (explicitClips.Any(c => !model.AnimationList.Any(a => a.Name == c.m_Name && a.TrackList.Count > 0)))
                throw new InvalidDataException("选中的动画没有生成有效轨道，请检查模型与动画是否匹配。");
            token.ThrowIfCancellationRequested();
            var folder = Path.Combine(exportRoot, SafeName(root.m_Name) + "-" + unchecked((ulong)root.m_PathID).ToString("X") + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            var incomplete = Path.Combine(folder, "export-incomplete.txt");
            File.WriteAllText(incomplete, "导出尚未完成；完成后此文件会自动移除。若任务失败或取消，请查看 Asset Explorer 中的错误信息。");
            var fbx = Path.Combine(folder, SafeName(root.m_Name) + ".fbx");
            var cwd = Environment.CurrentDirectory;
            try { ModelExporter.ExportFbx(fbx, model, options.Fbx with { exportAnimations = true, exportSkins = true, preserveRootNodeAsNull = true }); }
            finally { Environment.CurrentDirectory = cwd; }
            if (!File.Exists(fbx) || new FileInfo(fbx).Length == 0) throw new IOException("FBX 导出器没有生成文件：" + fbx);
            var materialFolder = Path.Combine(folder, "Materials");
            Directory.CreateDirectory(materialFolder);
            // ZZZ can assign materials/textures at runtime, outside Renderer.m_Materials.
            // Preserve the complete loaded dependency set as sidecars as well as FBX-bound textures.
            var materials = manager.assetsFileList.SelectMany(f => f.Objects.OfType<Material>()).ToHashSet();
            foreach (var material in materials)
                File.WriteAllText(Path.Combine(materialFolder, SafeName(material.m_Name) + "-" + material.m_PathID + ".json"),
                    JsonConvert.SerializeObject((object)material.ToType() ?? material, Formatting.Indented));
            var textureFolder = Path.Combine(folder, "Textures");
            Directory.CreateDirectory(textureFolder);
            foreach (var texture in manager.assetsFileList.SelectMany(f => f.Objects.OfType<Texture2D>()))
            {
                token.ThrowIfCancellationRequested();
                using var image = texture.ConvertToStream(ImageFormat.Png, true);
                if (image == null) throw new InvalidDataException("贴图无法转换：" + texture.m_Name);
                var textureFile = Path.Combine(textureFolder, SafeName(texture.m_Name) + "-" + unchecked((ulong)texture.m_PathID).ToString("X") + ".png");
                using var target = File.Create(textureFile);
                image.Position = 0; image.CopyTo(target);
            }
            var output = new ExportedModel(fbx, model.AnimationList.Select(a => a.Name).ToArray(),
                Directory.GetFiles(folder, "*.png", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(folder, p)).ToArray(), model.MeshList.Count);
            File.WriteAllText(Path.Combine(folder, "export-report.json"), JsonConvert.SerializeObject(new
            {
                Model = output, Source = root.assetsFile.originalPath, Cab = root.assetsFile.fileName,
                Offset = root.assetsFile.offset, PathID = root.m_PathID,
                Selected = selected, Materials = materials.Select(m => m.m_Name).ToArray()
            }, Formatting.Indented));
            outputs.Add(output);
            File.Delete(incomplete);
            report?.Invoke($"已导出 {root.m_Name}：{output.Meshes} 网格、{output.Animations.Length} 动画、{output.Textures.Length} 贴图");
        }
        return outputs;
    }

    internal static List<GameObject> ResolveRoots(AssetsManager manager, AnimeStudio.Object[] selected)
    {
        var roots = new HashSet<GameObject>();
        var gameObjects = manager.assetsFileList.SelectMany(f => f.Objects.OfType<GameObject>()).ToArray();
        void Add(GameObject go)
        {
            var seen = new HashSet<GameObject>();
            while (go?.m_Transform != null && seen.Add(go))
            {
                if (!go.m_Transform.m_Father.TryGet(out var parent) || !parent.m_GameObject.TryGet(out var parentGo)) break;
                go = parentGo;
            }
            if (go != null && go.HasModel()) roots.Add(go);
        }
        foreach (var obj in selected)
        {
            switch (obj)
            {
                case GameObject go: Add(go); break;
                case Component component:
                    if (component.m_GameObject.TryGet(out var owner)) Add(owner);
                    break;
                case Mesh mesh:
                    foreach (var candidate in gameObjects)
                        if (candidate.m_SkinnedMeshRenderer?.m_Mesh.TryGet(out var skinned) == true && ReferenceEquals(mesh, skinned) ||
                            candidate.m_MeshFilter?.m_Mesh.TryGet(out var filtered) == true && ReferenceEquals(mesh, filtered)) Add(candidate);
                    break;
            }
        }
        // A clip selected on its own can use a model in its own CAB, never an unrelated loaded model.
        if (roots.Count == 0)
        {
            var files = selected.OfType<AnimationClip>().Select(c => c.assetsFile).ToHashSet();
            foreach (var go in gameObjects.Where(go => files.Contains(go.assetsFile))) Add(go);
        }
        return roots.OrderBy(g => g.m_Name, StringComparer.Ordinal).ThenBy(g => g.m_PathID).ToList();
    }

    private static IEnumerable<GameObject> Hierarchy(GameObject root)
    {
        var work = new Stack<GameObject>(); var seen = new HashSet<GameObject>(); work.Push(root);
        while (work.TryPop(out var go))
        {
            if (!seen.Add(go)) continue;
            yield return go;
            if (go.m_Transform == null) continue;
            foreach (var pointer in go.m_Transform.m_Children)
                if (pointer.TryGet(out var child) && child.m_GameObject.TryGet(out var childGo)) work.Push(childGo);
        }
    }

    private static void CollectController(RuntimeAnimatorController controller, HashSet<AnimationClip> clips)
    {
        if (controller is AnimatorController basic)
            foreach (var ptr in basic.m_AnimationClips) if (ptr.TryGet(out var clip)) clips.Add(clip);
        if (controller is AnimatorOverrideController overrides)
        {
            if (overrides.m_Controller.TryGet(out var original)) CollectController(original, clips);
            foreach (var pair in overrides.m_Clips)
                if (pair.m_OverrideClip.TryGet(out var clip)) clips.Add(clip);
        }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var value = new string((name ?? "model").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (value.Length == 0) value = "model";
        return "asset_" + value[..Math.Min(value.Length, 80)];
    }
}
