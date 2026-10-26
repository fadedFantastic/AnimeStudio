using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AnimeStudio;
using AnimeStudio.AssetExplorer;
using Newtonsoft.Json.Linq;

internal static class ControllerExportTests
{
    private static T Empty<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static uint Hash(string value) => SevenZip.CRC.CalculateDigestUTF8(value);

    public static void Run(string output, Action<bool, string> check)
    {
        var folder = Path.Combine(output, "controllers"); Directory.CreateDirectory(folder);
        var graph = Graph(); var requested = new List<int>();
        var converted = new ControllerYamlConverter(graph, index => { requested.Add(index); return ControllerYamlConverter.Ref(0); }).Convert();
        check(converted.Layers == 1 && converted.States == 2 && converted.StateMachines == 2 && converted.BlendTrees == 1,
            "editable controller preserves nested machines, states and blend trees");
        check(requested.SequenceEqual(new[] { 0, 1 }), "blend tree leaf indexes preserve clip references and order");
        check(converted.Text.Contains("m_IsExit: 1") && converted.Text.Contains("m_ConditionEvent: Speed") &&
            converted.Text.Contains("m_TransitionDuration: 0.25") && converted.Text.Contains("m_TimeParameterActive: 1"),
            "state exit, condition, timing and time parameter survive editor conversion");
        var anchors = Regex.Matches(converted.Text, @"--- !u!\d+ &(\d+)").Select(m => m.Groups[1].Value).ToHashSet();
        check(Regex.Matches(converted.Text, @"fileID: (\d+)").Select(m => m.Groups[1].Value)
                .All(id => id == "0" || anchors.Contains(id)), "all generated controller object references resolve");
        File.WriteAllText(Path.Combine(folder, "Nested.controller"), converted.Text);
        var state = graph.m_Controller.m_StateMachineArray[0].m_StateConstantArray[0];
        var condition = state.m_TransitionConstantArray[0].m_ConditionConstantArray[0];
        var originalEvent = condition.m_EventID;
        var frameHash = Hash("FrameCount"); graph.m_TOS[frameHash] = "FrameCount";
        var frame = Empty<ValueConstant>(); frame.m_ID = frameHash; frame.m_Type = 3;
        graph.m_Controller.m_Values.m_ValueArray.Add(frame); graph.m_Controller.m_DefaultValues.m_IntValues = [0];
        graph.assetsFile.game = GameManager.GetGameByType(GameType.ZZZ); condition.m_ConditionMode = 9;
        condition.m_EventID = frameHash; condition.m_EventThreshold = 90;
        var inclusive = new ControllerYamlConverter(graph, _ => ControllerYamlConverter.Ref(0)).Convert();
        check(inclusive.Text.Contains("m_ConditionMode: 3") && inclusive.Text.Contains("m_EventTreshold: 89"),
            "ZZZ integer frame boundary survives inclusive-to-standard condition conversion");
        condition.m_EventID = originalEvent;
        try { new ControllerYamlConverter(graph, _ => ControllerYamlConverter.Ref(0)).Convert(); throw new Exception("float inclusive condition approximated"); }
        catch (NotSupportedException) { check(true, "unsupported floating-point inclusive condition is not silently approximated"); }
        condition.m_ConditionMode = 3; condition.m_EventThreshold = .5f;
        state.m_TransitionConstantArray[0].m_DestinationState = 30099;
        try { new ControllerYamlConverter(graph, _ => ControllerYamlConverter.Ref(0)).Convert(); throw new Exception("invalid selector accepted"); }
        catch (InvalidDataException) { check(true, "invalid compiled selector is rejected instead of producing a broken graph"); }
        state.m_TransitionConstantArray[0].m_DestinationState = 30003;
        state.m_BlendTreeConstantArray[0].m_NodeArray[0].m_ChildIndices[0] = 0;
        try { new ControllerYamlConverter(graph, _ => ControllerYamlConverter.Ref(0)).Convert(); throw new Exception("cyclic blend tree accepted"); }
        catch (InvalidDataException) { check(true, "cyclic blend tree is rejected"); }
        CheckRaw(folder, check);
        CheckContext(check);
    }

    private static void CheckRaw(string folder, Action<bool, string> check)
    {
        var data = Enumerable.Range(0, 200000).Select(i => (byte)(i * 37)).ToArray();
        using var stream = new CancelStream(data); using var reader = new FileReader(Path.Combine(folder, "CAB-fixture"), stream);
        var file = Empty<SerializedFile>(); file.reader = reader; file.fullName = reader.FullPath; file.originalPath = reader.FullPath;
        file.fileName = "CAB-fixture"; file.game = GameManager.GetGameByType(GameType.ZZZ); file.unityVersion = "2019.4.40f1";
        file.header = new SerializedFileHeader { m_FileSize = data.Length, m_Version = SerializedFileFormatVersion.StoresTypeDependencies };
        file.m_Externals = [new() { fileName = "CAB-external", pathName = "archive:/CAB-external", guid = Guid.NewGuid(), type = 2 }];
        var info = new ObjectInfo { m_PathID = long.MinValue + 7, byteStart = 128, byteSize = 100000, classID = (int)ClassIDType.AnimatorController };
        file.m_Objects = [info, new() { m_PathID = 2, byteStart = 100128, byteSize = 40, classID = (int)ClassIDType.MonoBehaviour }];
        var controller = Empty<AnimatorController>(); controller.assetsFile = file; controller.m_PathID = info.m_PathID;
        controller.type = ClassIDType.AnimatorController; controller.m_Name = "RawFixture";
        var destination = Path.Combine(folder, "Raw.controller.bytes"); reader.Position = 53;
        CompiledControllerExporter.Export(controller, destination, "Assets/Raw.controller", default);
        check(File.ReadAllBytes(destination).SequenceEqual(data.Skip(128).Take(100000)) &&
            File.ReadAllBytes(destination + ".cab").SequenceEqual(data) && reader.Position == 53,
            "compiled export retains exact object and CAB bytes and restores input position");
        var meta = JObject.Parse(File.ReadAllText(destination + ".info.json"));
        check((string)meta["PathID"] == info.m_PathID.ToString() && meta["PathID"].Type == JTokenType.String &&
            meta["Objects"].Count() == 2 && (string)meta["ExternalFiles"][0]["Cab"] == "CAB-external" &&
            (string)meta["MainSHA256"] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))),
            "raw metadata keeps lossless PathIDs, companion objects, external references and checksum");
        var manager = new AssetsManager(); manager.assetsFileList.Add(file);
        var row = new CatalogAsset { Source = file.originalPath, Cab = file.fileName, PathID = info.m_PathID, Type = ClassIDType.AnimatorController };
        var unparsedFolder = Path.Combine(folder, "unparsed");
        var unparsed = CompiledControllerExporter.Export(manager, new("Assets/Unparsed.controller", [row]), [row], unparsedFolder, default);
        check(File.ReadAllBytes(unparsed.MainFiles.Single()).SequenceEqual(data.Skip(128).Take(100000)),
            "raw controller exports directly from metadata without deserializing the controller");
        try { CompiledControllerExporter.Export(controller, destination, "", default); throw new Exception("overwrite accepted"); }
        catch (IOException) { check(File.ReadAllBytes(destination).SequenceEqual(data.Skip(128).Take(100000)), "existing raw export is preserved"); }
        CheckOverrides(folder, file, info, check);
        info.byteStart = data.Length - 10;
        try { CompiledControllerExporter.Export(controller, Path.Combine(folder, "Invalid.bytes"), "", default); throw new Exception("invalid range accepted"); }
        catch (InvalidDataException) { check(!File.Exists(Path.Combine(folder, "Invalid.bytes")), "invalid raw ranges leave no output"); }
        info.byteStart = 128; using var cancellation = new CancellationTokenSource(); stream.OnRead = cancellation.Cancel;
        var cancelled = Path.Combine(folder, "Cancelled.bytes");
        try { CompiledControllerExporter.Export(controller, cancelled, "", cancellation.Token); throw new Exception("cancellation ignored"); }
        catch (OperationCanceledException) { check(!Directory.GetFiles(folder, "Cancelled.bytes*").Any() && reader.Position == 53, "mid-copy cancellation rolls back raw files and restores stream"); }
    }

    private static void CheckOverrides(string folder, SerializedFile file, ObjectInfo baseInfo, Action<bool, string> check)
    {
        var controller = Graph(); controller.assetsFile = file; controller.m_PathID = baseInfo.m_PathID;
        controller.type = ClassIDType.AnimatorController; controller.m_AnimationClips = [];
        foreach (var state in controller.m_Controller.m_StateMachineArray[0].m_StateConstantArray) state.m_BlendTreeConstantArray.Clear();
        var over = Empty<AnimatorOverrideController>(); over.assetsFile = file; over.m_PathID = 3;
        over.type = ClassIDType.AnimatorOverrideController; over.m_Name = "Overrides";
        over.m_Controller = new(0, controller.m_PathID, file); over.m_Clips = [];
        var info = new ObjectInfo { m_PathID = 3, classID = (int)ClassIDType.AnimatorOverrideController, byteStart = 130000, byteSize = 1000 };
        file.m_Objects.Add(info); file.ObjectsDic = new() { [controller.m_PathID] = controller, [3] = over };
        var result = EditableControllerExporter.Export(over, Path.Combine(folder, "Overrides.controller"), "Assets/Overrides.overrideController", default);
        check(result.MainFiles.Single().EndsWith(".overrideController") &&
            File.ReadAllText(result.MainFiles[0]).Contains("AnimatorOverrideController:") &&
            Directory.GetFiles(Path.Combine(folder, "Overrides_dependencies"), "*.controller").Length == 1,
            "editable override controller exports its base controller and native extension");
        over.m_Controller = new(0, over.m_PathID, file);
        try { EditableControllerExporter.Export(over, Path.Combine(folder, "Cycle.controller"), "", default); throw new Exception("controller cycle accepted"); }
        catch (InvalidDataException) { check(!Directory.GetFiles(folder, "Cycle*", SearchOption.AllDirectories).Any(), "controller reference cycles leave no partial published package"); }
    }

    private static void CheckContext(Action<bool, string> check)
    {
        var builder = new AssetIndexStore.Builder();
        foreach (var path in new[] { "Assets/Hero/Hero.prefab", "Assets/Heroine/Hero.prefab", "Assets/Hero/Sub/Hero.prefab", "12345" })
            builder.Add(new() { Name = "Hero", Type = ClassIDType.Animator, Container = path, Source = @"E:\" + path.GetHashCode() + ".blk" });
        var store = builder.Build(GameType.ZZZ, "fixture"); store.Guessed = new string[store.Count]; store.Guessed[^1] = "Assets/Hero/Guessed.prefab";
        var context = store.FindControllerContext("Assets/Hero/Hero.controller", default);
        check(context.Length == 1 && context[0].Container == "Assets/Hero/Hero.prefab",
            "controller context uses exact prefab directory boundaries and excludes guessed paths");
        check(new PrimaryAssetGroup("Assets/Hero/Hero.overrideController", []).IsCompiledController,
            "override controllers use the same explicit controller export path");
    }

    private sealed class CancelStream(byte[] bytes) : MemoryStream(bytes)
    {
        public Action OnRead;
        public override int Read(byte[] buffer, int offset, int count) { var result = base.Read(buffer, offset, count); OnRead?.Invoke(); return result; }
    }

    private static AnimatorController Graph()
    {
        var c = Empty<AnimatorController>(); c.version = [2019, 4, 40, 1]; c.m_Name = "Nested";
        c.assetsFile = Empty<SerializedFile>(); c.assetsFile.game = GameManager.GetGameByType(GameType.Normal);
        c.m_TOS = new();
        uint Name(string s) { var h = Hash(s); c.m_TOS[h] = s; return h; }
        var root = Name("Base Layer"); var nested = Hash("Base Layer.Sub"); var speed = Name("Speed");
        StateConstant State(string name, string full)
        {
            var s = Empty<StateConstant>(); s.m_NameID = Name(name); s.m_FullPathID = Name(full); s.m_Speed = 1;
            s.m_TransitionConstantArray = []; s.m_BlendTreeConstantIndexArray = [0]; s.m_BlendTreeConstantArray = [];
            s.m_WriteDefaultValues = true; return s;
        }
        var first = State("Idle", "Base Layer.Sub.Idle"); first.m_TimeParamID = speed;
        var second = State("Done", "Base Layer.Done");
        var transition = Empty<TransitionConstant>(); transition.m_DestinationState = 30003; transition.m_TransitionDuration = .25f;
        var condition = Empty<ConditionConstant>(); condition.m_ConditionMode = 3; condition.m_EventID = speed; condition.m_EventThreshold = .5f;
        transition.m_ConditionConstantArray = [condition]; first.m_TransitionConstantArray.Add(transition);
        BlendTreeNodeConstant Leaf(uint index) { var n = Empty<BlendTreeNodeConstant>(); n.m_ChildIndices = []; n.m_ClipID = index; n.m_Duration = 1; return n; }
        var blend = Empty<BlendTreeNodeConstant>(); blend.m_ChildIndices = [1, 2]; blend.m_BlendEventID = speed;
        blend.m_Blend1dData = Empty<Blend1dDataConstant>(); blend.m_Blend1dData.m_ChildThresholdArray = [0, 1];
        var tree = Empty<BlendTreeConstant>(); tree.m_NodeArray = [blend, Leaf(0), Leaf(1)]; first.m_BlendTreeConstantArray = [tree];
        SelectorStateConstant Selector(uint hash, bool entry, uint destination)
        {
            var s = Empty<SelectorStateConstant>(); s.m_FullPathID = hash; s.m_isEntry = entry;
            var t = Empty<SelectorTransitionConstant>(); t.m_Destination = destination; t.m_ConditionConstantArray = [];
            s.m_TransitionConstantArray = [t]; return s;
        }
        var machine = Empty<StateMachineConstant>(); machine.m_StateConstantArray = [first, second]; machine.m_AnyStateTransitionConstantArray = [];
        machine.m_SelectorStateConstantArray = [Selector(root, true, 1), Selector(root, false, 30000), Selector(nested, true, 0), Selector(nested, false, 1)];
        var layer = Empty<LayerConstant>(); layer.m_Binding = root;
        c.m_Controller = Empty<ControllerConstant>(); c.m_Controller.m_LayerArray = [layer]; c.m_Controller.m_StateMachineArray = [machine];
        var value = Empty<ValueConstant>(); value.m_ID = speed; value.m_Type = 1;
        c.m_Controller.m_Values = Empty<ValueArrayConstant>(); c.m_Controller.m_Values.m_ValueArray = [value];
        c.m_Controller.m_DefaultValues = Empty<ValueArray>(); c.m_Controller.m_DefaultValues.m_FloatValues = [.1f];
        return c;
    }
}
