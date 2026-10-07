using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnimeStudio.AssetExplorer;
using Newtonsoft.Json;

namespace AnimeStudio.GUI;

partial class MainForm
{
    private void InitializeAssetExplorer()
    {
        var item = new ToolStripMenuItem("Open Asset Explorer (Fast)");
        item.Click += (_, _) => OpenAssetExplorer();
        miscToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
        miscToolStripMenuItem.DropDownItems.Add(item);
    }

    internal void OpenAssetExplorer()
    {
        using var explorer = new ExplorerForm(new ExplorerBridge(this));
        explorer.ShowDialog(this);
    }

    internal sealed class ExplorerBridge(MainForm form) : IStudioBridge
    {
        private async Task<LoadPlan> Load(CatalogRequest request, bool requireDependencies, CancellationToken token)
        {
            if (requireDependencies && string.IsNullOrWhiteSpace(request.CabMap))
                throw new InvalidOperationException("FBX 导出需要 CABMap 才能自动找到贴图和材质。请建立索引或选择已有 CABMap。");
            var plan = await Task.Run(() => CabCatalog.Plan(request, token), token);
            if (plan.Missing.Length > 0) throw new FileNotFoundException(string.Join(Environment.NewLine, plan.Missing.Take(12)));
            token.ThrowIfCancellationRequested();
            form.updateGame(request.Game);
            var manager = Studio.assetsManager;
            var resolve = manager.ResolveDependencies;
            var types = JsonConvert.DeserializeObject<Dictionary<ClassIDType, (bool, bool)>>(Properties.Settings.Default.types);
            try
            {
                foreach (var type in new[] { ClassIDType.GameObject, ClassIDType.Transform, ClassIDType.RectTransform,
                    ClassIDType.Mesh, ClassIDType.MeshRenderer, ClassIDType.MeshFilter, ClassIDType.SkinnedMeshRenderer,
                    ClassIDType.Animator, ClassIDType.Animation, ClassIDType.AnimationClip, ClassIDType.AnimatorController,
                    ClassIDType.AnimatorOverrideController, ClassIDType.Avatar, ClassIDType.Material, ClassIDType.Texture2D, ClassIDType.AssetBundle, ClassIDType.MonoBehaviour })
                    TypeFlags.SetType(type, true, type.CanExport());
                manager.ResolveDependencies = false; // The plan already contains the exact transitive CAB offsets.
                manager.FilterData = new AssetsManager.AssetFilterData { Items = plan.Offsets.ToList() };
                using (token.Register(() => manager.tokenSource.Cancel()))
                    await Task.Run(() => manager.LoadFiles(plan.Files, mergeSplitAssets: false), token);
                token.ThrowIfCancellationRequested();
                foreach (var asset in plan.Selected) CabCatalog.Find(manager, asset);
                await Task.Run(() => SeparateMeshSupport.CompleteLoad(manager, request, plan.Selected, Logger.Info, token), token);
            }
            catch
            {
                form.ResetForm();
                throw;
            }
            finally
            {
                manager.ResolveDependencies = resolve;
                manager.FilterData = new AssetsManager.AssetFilterData { Items = new() };
                TypeFlags.SetTypes(types);
            }
            return plan;
        }

        public async Task LoadAsync(CatalogRequest request, CancellationToken token)
        {
            var plan = await Load(request, false, token);
            await form.BuildAssetStructures(plan.Files);
        }

        public async Task<string> ExportAssetsAsync(CatalogRequest request, string folder, CancellationToken token)
        {
            var plan = await Load(request, false, token);
            int exported = 0, skipped = 0;
            try
            {
                await Task.Run(() =>
                {
                    foreach (var asset in plan.Selected)
                    {
                        token.ThrowIfCancellationRequested();
                        var obj = CabCatalog.Find(Studio.assetsManager, asset);
                        var output = Path.Combine(folder, asset.Type.ToString()) + Path.DirectorySeparatorChar;
                        if (Exporter.ExportConvertFile(new AssetItem(obj), output)) exported++; else skipped++;
                    }
                }, token);
                return $"导出完成：{exported} 个资源，{skipped} 个跳过（已存在或该类型不支持单独转换）。目录：{folder}";
            }
            finally { await form.BuildAssetStructures(plan.Files); }
        }

        public async Task<string> ExportModelsAsync(CatalogRequest request, string folder, CancellationToken token)
        {
            var plan = await Load(request, true, token);
            var options = GetModelOptions();
            try
            {
                var result = await Task.Run(() => ModelExportService.Export(Studio.assetsManager, plan.Selected,
                    folder, options, Logger.Info, token), token);
                return $"已导出 {result.Count} 个 FBX，包含 {result.Sum(x => x.Animations.Length)} 段动画、{result.Sum(x => x.Textures.Length)} 张贴图。目录：{folder}";
            }
            finally { await form.BuildAssetStructures(plan.Files); }
        }

        public Task<DirectoryExportResult> ExportDirectoryAsync(DirectoryExportPlan plan, CatalogRequest context,
            string folder, Action<string> report, CancellationToken token)
            => DirectoryExportWorker.ExportAsync(plan, context, folder, GetModelOptions(), report, token);

        internal static PrimaryExportResult ExportPrimaryObject(PrimaryAssetGroup group, PrimaryAssetContent content,
            string output, CancellationToken token)
        {
            if (content.Main == null) throw new InvalidDataException("没有找到主资源对象。");
            var members = content.Members;
            var mainFiles = new List<string>();
            if (members.Length == 1 && content.Main is not GameObject and not MonoBehaviour)
            {
                var item = new AssetItem(content.Main) { Text = ResourcePaths.SafeSegment(Path.GetFileNameWithoutExtension(group.ResourcePath)) };
                if (!Exporter.ExportConvertFile(item, output + Path.DirectorySeparatorChar))
                    if (!Exporter.ExportRawFile(item, output + Path.DirectorySeparatorChar)) throw new IOException("主资源未能导出。");
                mainFiles.AddRange(Directory.GetFiles(output).Where(p => Path.GetFileName(p) != "export-incomplete.txt"));
            }
            else
            {
                // Composite Unity assets have no general native-file writer. Preserve the primary
                // object and all its subasset data in one JSON document instead of scattering them.
                var file = Path.Combine(output, ResourcePaths.SafeSegment(Path.GetFileName(group.ResourcePath)) + ".json");
                using var text = File.CreateText(file);
                using var writer = new JsonTextWriter(text) { Formatting = Formatting.Indented };
                var serializer = new JsonSerializer();
                writer.WriteStartObject();
                writer.WritePropertyName("ResourcePath"); writer.WriteValue(group.ResourcePath);
                writer.WritePropertyName("MainPathID"); writer.WriteValue(content.Main.m_PathID);
                writer.WritePropertyName("Assets"); writer.WriteStartArray();
                foreach (var asset in members)
                {
                    token.ThrowIfCancellationRequested();
                    writer.WriteStartObject();
                    writer.WritePropertyName("PathID"); writer.WriteValue(asset.m_PathID);
                    writer.WritePropertyName("Type"); writer.WriteValue(asset.type.ToString());
                    writer.WritePropertyName("Name"); writer.WriteValue(asset.Name);
                    writer.WritePropertyName("Source"); serializer.Serialize(writer, PrimaryAssetResolver.Reference(asset));
                    writer.WritePropertyName("Data"); serializer.Serialize(writer, (object)asset.ToType() ?? asset);
                    writer.WritePropertyName("RawData"); writer.WriteValue(Convert.ToBase64String(asset.GetRawData()));
                    byte[] payload = asset switch
                    {
                        Texture2D texture => texture.image_data.GetData(),
                        AudioClip audio => audio.m_AudioData.GetData(),
                        VideoClip video => video.m_VideoData.GetData(),
                        _ => null
                    };
                    if (payload != null) { writer.WritePropertyName("ResourceData"); writer.WriteValue(payload); }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
                mainFiles.Add(file);
            }
            File.WriteAllText(Path.Combine(output, "primary-resource.json"), JsonConvert.SerializeObject(new
                { group.ResourcePath, SubAssets = group.Members, MainFiles = mainFiles.Select(Path.GetFileName).ToArray() }, Formatting.Indented));
            return new(mainFiles.ToArray());
        }

        private static ModelExportService.Options GetModelOptions()
        {
            var settings = Properties.Settings.Default;
            return new ModelExportService.Options(new ModelConverter.Options
            {
                imageFormat = ImageFormat.Png, collectAnimations = true, exportMaterials = true,
                uvs = JsonConvert.DeserializeObject<Dictionary<string, (bool, int)>>(settings.uvs),
                texs = JsonConvert.DeserializeObject<Dictionary<string, int>>(settings.texs)
            }, new Fbx.ExportOptions
            {
                eulerFilter = settings.eulerFilter, filterPrecision = (float)settings.filterPrecision,
                exportAllNodes = settings.exportAllNodes, exportSkins = true, exportAnimations = true,
                exportBlendShape = settings.exportBlendShape, castToBone = settings.castToBone,
                boneSize = (int)settings.boneSize, scaleFactor = (float)settings.scaleFactor,
                fbxVersion = settings.fbxVersion, fbxFormat = settings.fbxFormat
            });
        }
    }
}
