using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace AnimeStudio.AssetExplorer;

public static class EditableControllerExporter
{
    public static PrimaryExportResult Export(AssetsManager manager, PrimaryAssetGroup group, CatalogAsset[] selected,
        string output, CancellationToken token)
    {
        var located = CompiledControllerExporter.Locate(manager, group, selected, token);
        var controller = ReadController(located.File, located.Info);
        var destination = Path.Combine(output, ResourcePaths.OutputSegment(Path.GetFileName(group.ResourcePath)));
        return Export(controller, destination, group.ResourcePath, token);
    }

    public static PrimaryExportResult Export(AnimeStudio.Object controller, string destination, string resourcePath, CancellationToken token)
    {
        if (controller is not RuntimeAnimatorController) throw new ArgumentException("资源不是动画控制器。");
        token.ThrowIfCancellationRequested(); destination = Path.GetFullPath(destination);
        if (controller is AnimatorOverrideController) destination = Path.ChangeExtension(destination, ".overrideController");
        if (File.Exists(destination)) throw new IOException("目标文件已存在，未覆盖：" + destination);
        var staging = Directory.CreateTempSubdirectory("aex-controller-"); var created = new List<string>();
        try
        {
            var session = new Session(staging.FullName, Path.GetFileName(destination), resourcePath, token);
            session.Controller((RuntimeAnimatorController)controller, Path.GetFileName(destination)); token.ThrowIfCancellationRequested();
            var folder = Path.GetDirectoryName(destination);
            var files = Directory.GetFiles(staging.FullName, "*", SearchOption.AllDirectories)
                .Select(source => (Source: source, Target: ResourcePaths.ContainedPath(folder, Path.GetRelativePath(staging.FullName, source)))).ToArray();
            foreach (var file in files) if (File.Exists(file.Target)) throw new IOException("目标文件已存在，未覆盖：" + file.Target);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(file.Target));
                using var source = File.OpenRead(file.Source); using var target = new FileStream(file.Target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created.Add(file.Target); source.CopyTo(target);
            }
            token.ThrowIfCancellationRequested(); return new([destination], session.AnimationCount);
        }
        catch { foreach (var path in created) File.Delete(path); throw; }
        finally
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(staging.FullName).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) staging.Delete(true);
        }
    }

    private static RuntimeAnimatorController ReadController(SerializedFile file, ObjectInfo info)
    {
        if (file.ObjectsDic.TryGetValue(info.m_PathID, out var obj) && obj is RuntimeAnimatorController existing) return existing;
        return Read(file, info, reader => info.classID == (int)ClassIDType.AnimatorController
            ? (RuntimeAnimatorController)new AnimatorController(reader) : new AnimatorOverrideController(reader));
    }
    private static T Read<T>(SerializedFile file, ObjectInfo info, Func<ObjectReader, T> read)
    {
        var position = file.reader.Position;
        try { return read(new ObjectReader(file.reader, file, info, file.game)); }
        finally { file.reader.Position = position; }
    }

    private sealed class Session(string folder, string mainName, string resource, CancellationToken token)
    {
        private readonly string dependencyFolder = Path.GetFileNameWithoutExtension(mainName) + "_dependencies";
        private readonly string rawFolder = Path.GetFileNameWithoutExtension(mainName) + "_compiled";
        private readonly Dictionary<(string Cab, long ID), YAMLMappingNode> clips = [], controllers = [];
        private readonly HashSet<(string Cab, long ID)> active = [];
        public int AnimationCount => clips.Count;
        private sealed record AnimationDiagnostic(string Name, string File, string PathID, int CurveCount, string[] UnresolvedPaths);
        private readonly List<AnimationDiagnostic> animationDiagnostics = [];

        public YAMLMappingNode Controller(RuntimeAnimatorController controller, string relative = null)
        {
            token.ThrowIfCancellationRequested(); var identity = (controller.assetsFile.fileName, controller.m_PathID);
            if (active.Contains(identity)) throw new InvalidDataException("覆盖控制器引用包含循环。");
            if (controllers.TryGetValue(identity, out var cached)) return cached;
            active.Add(identity);
            relative ??= Path.Combine(dependencyFolder, ResourcePaths.SafeSegment(controller.Name) + "-" + GuidFor(controller)[..8] +
                (controller is AnimatorOverrideController ? ".overrideController" : ".controller"));
            var guid = GuidFor(controller); var file = Path.Combine(folder, relative);
            var reference = ControllerYamlConverter.Ref(controller is AnimatorController ? ControllerYamlConverter.ControllerID : 22100000, guid);
            controllers.Add(identity, reference);
            var raw = Path.Combine(folder, rawFolder, Path.GetFileName(relative) + ".bytes");
            CompiledControllerExporter.Export(controller, raw, relative == mainName ? resource : "", token);
            if (controller is AnimatorController animator)
            {
                var animationReferences = animator.m_AnimationClips.Select(p => ResolveClip(animator.assetsFile, p)).ToArray();
                YAMLMappingNode Motion(int index) => index >= 0 && index < animationReferences.Length ? animationReferences[index]
                    : throw new InvalidDataException("控制器动画索引越界：" + index);
                var graph = new ControllerYamlConverter(animator, Motion).Convert(); Write(file, graph.Text); Meta(file, guid);
                var warnings = graph.Warnings.Concat(animationDiagnostics.Any(x => x.UnresolvedPaths.Length > 0) ?
                    new[] { "部分动画缺少骨骼路径，保留 path_哈希 曲线；播放前需要对应 Avatar/骨架重新绑定，详情见 Animations。" } : []).ToArray();
                Write(file + ".restore.json", JsonConvert.SerializeObject(new
                {
                    FormatVersion = 1, TargetUnityVersion = "2022.3", ResourcePath = resource,
                    graph.Layers, graph.States, graph.StateMachines, graph.Transitions, graph.BlendTrees,
                    Animations = animationDiagnostics, Warnings = warnings,
                    OriginalData = Path.GetRelativePath(Path.GetDirectoryName(file), raw).Replace('\\', '/'),
                    CompiledGraph = animator.m_Controller, Strings = animator.m_TOS
                }, Formatting.Indented));
            }
            else if (controller is AnimatorOverrideController over)
            {
                var baseObject = Resolve(over.assetsFile, over.m_Controller.m_FileID, over.m_Controller.m_PathID);
                if (baseObject.Info.classID != (int)ClassIDType.AnimatorController && baseObject.Info.classID != (int)ClassIDType.AnimatorOverrideController)
                    throw new InvalidDataException("覆盖控制器的基础资源不是控制器。");
                var node = ControllerYamlConverter.ObjectNode(over.Name, false); node.Add("m_Controller", Controller(ReadController(baseObject.File, baseObject.Info)));
                var overrides = new YAMLSequenceNode();
                foreach (var pair in over.m_Clips)
                {
                    var n = new YAMLMappingNode(); n.Add("m_OriginalClip", ResolveClip(over.assetsFile, pair.m_OriginalClip));
                    n.Add("m_OverrideClip", ResolveClip(over.assetsFile, pair.m_OverrideClip)); overrides.Add(n);
                }
                node.Add("m_Clips", overrides); var document = new YAMLDocument(); var root = document.CreateMappingRoot();
                root.Tag = "221"; root.Anchor = "22100000"; root.Add("AnimatorOverrideController", node);
                var writer = new YAMLWriter(); writer.AddDocument(document); using var text = new StringWriter(CultureInfo.InvariantCulture);
                writer.Write(text); Write(file, text.ToString()); Meta(file, guid);
            }
            active.Remove(identity); return reference;
        }

        private YAMLMappingNode ResolveClip(SerializedFile owner, PPtr<AnimationClip> pointer)
        {
            if (pointer.m_PathID == 0) return ControllerYamlConverter.Ref(0);
            token.ThrowIfCancellationRequested(); var located = Resolve(owner, pointer.m_FileID, pointer.m_PathID);
            if (located.Info.classID != (int)ClassIDType.AnimationClip) throw new InvalidDataException("运动引用不是 AnimationClip。");
            var identity = (located.File.fileName, located.Info.m_PathID);
            if (clips.TryGetValue(identity, out var cached)) return cached;
            var original = located.File.ObjectsDic.GetValueOrDefault(located.Info.m_PathID) as AnimationClip;
            var paths = original?.FindTOS();
            // Decode a fresh instance: ordinary .anim conversion mutates its curve lists.
            var animation = Read(located.File, located.Info, reader => new AnimationClip(reader));
            var guid = GuidFor(animation);
            var relative = Path.Combine(dependencyFolder, ResourcePaths.SafeSegment(animation.Name) + "-" + guid[..8] + ".anim");
            var file = Path.Combine(folder, relative); Write(file, animation.Convert(paths)); Meta(file, guid);
            var curvePaths = animation.m_RotationCurves.Select(c => c.path).Concat(animation.m_EulerCurves.Select(c => c.path))
                .Concat(animation.m_PositionCurves.Select(c => c.path)).Concat(animation.m_ScaleCurves.Select(c => c.path))
                .Concat(animation.m_FloatCurves.Select(c => c.path)).Concat(animation.m_PPtrCurves.Select(c => c.path)).ToArray();
            animationDiagnostics.Add(new(animation.Name, relative.Replace('\\', '/'), animation.m_PathID.ToString(CultureInfo.InvariantCulture),
                curvePaths.Length, curvePaths.Where(path => AnimationClipConverter.UnknownPathRegex.IsMatch(path)).Distinct().ToArray()));
            var reference = ControllerYamlConverter.Ref(7400000, guid); clips.Add(identity, reference); return reference;
        }

        private static (SerializedFile File, ObjectInfo Info) Resolve(SerializedFile owner, int fileID, long pathID)
        {
            SerializedFile target;
            if (fileID == 0) target = owner;
            else
            {
                if (fileID < 1 || fileID > owner.m_Externals.Count) throw new InvalidDataException("外部文件索引越界。");
                var name = owner.m_Externals[fileID - 1].fileName;
                var files = owner.assetsManager.assetsFileList.Where(f => f.fileName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (files.Length != 1) throw new InvalidDataException($"外部 CAB {name} 匹配到 {files.Length} 份，无法定位动画或基础控制器。");
                target = files[0];
            }
            var objects = target.m_Objects.Where(o => o.m_PathID == pathID).ToArray();
            if (objects.Length != 1) throw new InvalidDataException($"CAB {target.fileName} 的对象 {pathID} 匹配到 {objects.Length} 项。");
            return (target, objects[0]);
        }
        private string GuidFor(AnimeStudio.Object obj) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            mainName + "|" + obj.assetsFile.fileName + "|" + obj.m_PathID.ToString(CultureInfo.InvariantCulture) + "|" + (int)obj.type)))[..32].ToLowerInvariant();
        private static void Write(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(value);
        }
        private static void Meta(string path, string guid) => Write(path + ".meta", "fileFormatVersion: 2\nguid: " + guid +
            "\nNativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: " + (path.EndsWith(".anim") ? "7400000" : path.EndsWith(".overrideController") ? "22100000" : "9100000") +
            "\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n");
    }
}
