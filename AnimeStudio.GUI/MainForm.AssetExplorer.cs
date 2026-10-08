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
            token.ThrowIfCancellationRequested();
            // There is no general writer for Unity prefab/asset/material files. Do not disguise
            // JSON or raw object bytes as a successfully reconstructed source resource.
            if (content.Members.Length != 1 || content.Main is not (Texture2D or AudioClip or TextAsset or AnimeStudio.Font or Mesh or VideoClip or MovieTexture or Sprite or Shader or AnimationClip))
                return PrimaryExportResult.Skip("该主资源或复合资源暂不支持还原为可用文件，已跳过（不生成替代 JSON）：" + group.ResourcePath);
            var item = new AssetItem(content.Main)
            {
                Text = ResourcePaths.OutputSegment(Path.GetFileNameWithoutExtension(group.ResourcePath)),
                Container = group.ResourcePath
            };
            // Text payloads already are the source data; keep their original extension even if
            // the general Studio exporter is configured to use .txt instead.
            if (content.Main is TextAsset text)
            {
                var file = Path.Combine(output, ResourcePaths.OutputSegment(Path.GetFileName(group.ResourcePath)));
                File.WriteAllBytes(file, text.m_Script);
                return new([file]);
            }
            if (!Exporter.ExportConvertFile(item, output + Path.DirectorySeparatorChar)) throw new IOException("主资源未能转换为可用文件。");
            return new(Directory.GetFiles(output));
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
