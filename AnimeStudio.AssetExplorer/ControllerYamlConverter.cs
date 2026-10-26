using System.Globalization;

namespace AnimeStudio.AssetExplorer;

public sealed record ControllerYamlResult(string Text, int Layers, int States, int StateMachines, int Transitions,
    int BlendTrees, string[] Warnings);

// Compiled selector destinations (30000 + index) identify machine entries/exits.
// Rebuild an editor graph while retaining state/transition order; generate positions.
public sealed class ControllerYamlConverter
{
    public const long ControllerID = 9100000;
    private readonly AnimatorController controller;
    private readonly Func<int, YAMLMappingNode> clip;
    private readonly YAMLWriter writer = new();
    private readonly List<string> warnings = [];
    private long nextID = 100000;
    private int states, machines, transitions, trees;

    private sealed class Machine(string path, uint hash, long id)
    {
        public string Path = path;
        public uint Hash = hash;
        public long ID = id;
        public Machine Parent;
        public List<int> States = [];
        public List<Machine> Children = [];
        public List<SelectorTransitionConstant> Entry = [], Exit = [];
        public YAMLSequenceNode Entries = new(), Exits = new(), Any = new();
        public long Default;
    }

    public ControllerYamlConverter(AnimatorController controller, Func<int, YAMLMappingNode> clip)
    {
        this.controller = controller;
        this.clip = clip;
    }

    public ControllerYamlResult Convert()
    {
        if (controller.version[0] < 5) throw new NotSupportedException("可编辑控制器导出需要 Unity 5 或更新版本的编译布局。");
        var compiled = controller.m_Controller ?? throw new InvalidDataException("没有保留编译状态机数据。");
        var root = ObjectNode(controller.Name, false); root.Add("serializedVersion", 5);
        var parameters = new YAMLSequenceNode();
        foreach (var value in compiled.m_Values.m_ValueArray)
        {
            if (value.m_Type is not (1 or 3 or 4 or 9)) throw new NotSupportedException("不支持的状态机参数类型：" + value.m_Type);
            var node = new YAMLMappingNode();
            node.Add("m_Name", Name(value.m_ID)); node.Add("m_Type", value.m_Type);
            node.Add("m_DefaultFloat", value.m_Type == 1 ? At(compiled.m_DefaultValues.m_FloatValues, value.m_Index) : 0f);
            node.Add("m_DefaultInt", value.m_Type == 3 ? At(compiled.m_DefaultValues.m_IntValues, value.m_Index) : 0);
            node.Add("m_DefaultBool", value.m_Type is 4 or 9 && At(compiled.m_DefaultValues.m_BoolValues, value.m_Index));
            node.Add("m_Controller", Ref(ControllerID)); parameters.Add(node);
        }
        root.Add("m_AnimatorParameters", parameters);
        var layerNodes = new YAMLSequenceNode();
        for (var i = 0; i < compiled.m_LayerArray.Count; i++)
        {
            var layer = compiled.m_LayerArray[i];
            if (layer.m_StateMachineIndex >= compiled.m_StateMachineArray.Count) throw new InvalidDataException("层的状态机索引越界。");
            var name = Name(layer.m_Binding);
            var machine = BuildMachine(compiled.m_StateMachineArray[(int)layer.m_StateMachineIndex], name, layer.m_Binding,
                checked((int)layer.m_StateMachineMotionSetIndex));
            var node = new YAMLMappingNode(); node.Add("serializedVersion", 5); node.Add("m_Name", name);
            node.Add("m_StateMachine", Ref(machine)); node.Add("m_Mask", Ref(0)); node.Add("m_Motions", new YAMLSequenceNode());
            node.Add("m_Behaviours", new YAMLSequenceNode()); node.Add("m_BlendingMode", layer.m_LayerBlendingMode);
            node.Add("m_SyncedLayerIndex", -1); node.Add("m_DefaultWeight", layer.m_DefaultWeight);
            node.Add("m_IKPass", layer.m_IKPass); node.Add("m_SyncedLayerAffectsTiming", layer.m_SyncedLayerAffectsTiming);
            node.Add("m_Controller", Ref(ControllerID)); layerNodes.Add(node);
            if (layer.m_StateMachineMotionSetIndex != 0)
                warnings.Add($"层 {name} 的同步运动集已展开成独立可编辑层。");
            if (layer.m_SkeletonMask?.m_Data?.Count > 0 || layer.m_BodyMask is { } mask &&
                (mask.word0 != uint.MaxValue || mask.word1 != uint.MaxValue || (mask.word2 & 0x7FFFF) != 0x7FFFF))
                warnings.Add($"层 {name} 的编译遮罩保留在原始数据中；编辑器 AvatarMask 需要重新指定。");
        }
        root.Add("m_AnimatorLayers", layerNodes); Document(ClassIDType.AnimatorController, ControllerID, root);
        using var output = new StringWriter(CultureInfo.InvariantCulture); writer.Write(output);
        warnings.Add("编辑器节点位置重新生成；原始脚本行为不包含在此编辑器图中。");
        if (controller.assetsFile.game.Type is GameType.ZZZ or GameType.ZZZ_CB2)
            warnings.Add("ZZZ 扩展的帧数转换和自动偏移参数保留在原始数据中；标准 Unity 使用可恢复的常规转换字段。");
        return new(output.ToString(), compiled.m_LayerArray.Count, states, machines, transitions, trees, warnings.Distinct().ToArray());
    }

    private long BuildMachine(StateMachineConstant compiled, string layer, uint layerHash, int motionSet)
    {
        var sourceStates = compiled.m_StateConstantArray;
        var stateIDs = sourceStates.Select(_ => nextID++).ToArray(); states += stateIDs.Length;
        var paths = new Dictionary<uint, string> { [layerHash] = layer };
        var statePaths = sourceStates.Select(s => Name(s.m_FullPathID != 0 ? s.m_FullPathID : s.m_PathID)).ToArray();
        foreach (var path in statePaths)
        {
            var parent = Parent(path);
            while (!string.IsNullOrEmpty(parent)) { paths[Hash(parent)] = parent; parent = Parent(parent); }
        }
        var byHash = new Dictionary<uint, Machine>();
        var selectors = compiled.m_SelectorStateConstantArray ?? [];
        foreach (var selector in selectors)
        {
            if (!byHash.TryGetValue(selector.m_FullPathID, out var machine))
            {
                if (!paths.TryGetValue(selector.m_FullPathID, out var path))
                {
                    path = layer + ".Recovered_" + selector.m_FullPathID;
                    warnings.Add($"空状态机 {selector.m_FullPathID} 的名称和父层级无法确定，暂放在 {layer} 下。");
                }
                byHash.Add(selector.m_FullPathID, machine = new(path, selector.m_FullPathID, nextID++));
            }
            if (selector.m_isEntry) machine.Entry.AddRange(selector.m_TransitionConstantArray);
            else machine.Exit.AddRange(selector.m_TransitionConstantArray);
        }
        if (!byHash.TryGetValue(layerHash, out var root)) byHash.Add(layerHash, root = new(layer, layerHash, nextID++));
        foreach (var path in paths.Values.OrderBy(p => p.Count(c => c == '.')))
            if (!byHash.ContainsKey(Hash(path))) byHash.Add(Hash(path), new(path, Hash(path), nextID++));
        foreach (var machine in byHash.Values)
        {
            if (machine == root) continue;
            if (!byHash.TryGetValue(Hash(Parent(machine.Path)), out var parent)) parent = root;
            machine.Parent = parent; parent.Children.Add(machine);
        }
        machines += byHash.Count;
        for (var i = 0; i < sourceStates.Count; i++)
        {
            if (!byHash.TryGetValue(Hash(Parent(statePaths[i])), out var parent)) parent = root;
            parent.States.Add(i);
        }
        (long State, long Machine, bool Exit) Destination(uint destination)
        {
            if (destination < 30000)
            {
                if (destination >= stateIDs.Length) throw new InvalidDataException("转换的目标状态索引越界：" + destination);
                return (stateIDs[destination], 0, false);
            }
            var index = destination % 30000;
            if (index >= selectors.Count) throw new InvalidDataException("转换的选择器索引越界：" + destination);
            var selector = selectors[(int)index];
            return (0, selector.m_isEntry ? byHash[selector.m_FullPathID].ID : 0, !selector.m_isEntry);
        }
        long Transition(uint destination, List<ConditionConstant> conditions, TransitionConstant source = null)
        {
            if (destination == uint.MaxValue) return 0;
            var dest = Destination(destination); var node = ObjectNode(source == null ? "" : Name(source.m_UserID));
            node.Add("m_Conditions", Conditions(conditions)); node.Add("m_DstStateMachine", Ref(dest.Machine)); node.Add("m_DstState", Ref(dest.State));
            node.Add("m_Solo", false); node.Add("m_Mute", false); node.Add("m_IsExit", dest.Exit);
            if (source != null)
            {
                node.Add("serializedVersion", 3); node.Add("m_TransitionDuration", source.m_TransitionDuration);
                node.Add("m_TransitionOffset", source.m_TransitionOffset); node.Add("m_ExitTime", source.m_ExitTime);
                node.Add("m_HasExitTime", source.m_HasExitTime); node.Add("m_HasFixedDuration", source.m_HasFixedDuration);
                node.Add("m_InterruptionSource", source.m_InterruptionSource); node.Add("m_OrderedInterruption", source.m_OrderedInterruption);
                node.Add("m_CanTransitionToSelf", source.m_CanTransitionToSelf);
            }
            var id = nextID++; transitions++;
            Document(source == null ? ClassIDType.AnimatorTransition : ClassIDType.AnimatorStateTransition, id, node); return id;
        }
        foreach (var machine in byHash.Values)
        {
            for (var i = 0; i < machine.Entry.Count; i++)
            {
                var entry = machine.Entry[i];
                if (i == machine.Entry.Count - 1 && entry.m_ConditionConstantArray.Count == 0 && entry.m_Destination < stateIDs.Length)
                    machine.Default = stateIDs[entry.m_Destination];
                else { var id = Transition(entry.m_Destination, entry.m_ConditionConstantArray); if (id != 0) machine.Entries.Add(Ref(id)); }
            }
            if (machine.Entry.Count == 0 && machine == root && compiled.m_DefaultState < stateIDs.Length)
                machine.Default = stateIDs[compiled.m_DefaultState];
            if (machine.Parent != null)
            {
                var exits = new YAMLSequenceNode();
                foreach (var source in machine.Exit)
                { var id = Transition(source.m_Destination, source.m_ConditionConstantArray); if (id != 0) exits.Add(Ref(id)); }
                var pair = new YAMLMappingNode(); pair.Add("first", Ref(machine.ID)); pair.Add("second", exits); machine.Parent.Exits.Add(pair);
            }
            foreach (var index in machine.States)
            {
                var source = sourceStates[index]; var node = ObjectNode(Name(source.m_NameID));
                node.Add("serializedVersion", 6); node.Add("m_Speed", source.m_Speed); node.Add("m_CycleOffset", source.m_CycleOffset);
                var stateTransitions = new YAMLSequenceNode();
                foreach (var t in source.m_TransitionConstantArray)
                { var id = Transition(t.m_DestinationState, t.m_ConditionConstantArray, t); if (id != 0) stateTransitions.Add(Ref(id)); }
                node.Add("m_Transitions", stateTransitions); node.Add("m_StateMachineBehaviours", new YAMLSequenceNode());
                node.Add("m_Position", Position(0, 0)); node.Add("m_IKOnFeet", source.m_IKOnFeet);
                node.Add("m_WriteDefaultValues", source.m_WriteDefaultValues); node.Add("m_Mirror", source.m_Mirror);
                foreach (var (key, hash) in new[] { ("Speed", source.m_SpeedParamID), ("Mirror", source.m_MirrorParamID),
                    ("CycleOffset", source.m_CycleOffsetParamID), ("Time", source.m_TimeParamID) })
                { node.Add("m_" + key + "ParameterActive", hash is not (0 or uint.MaxValue)); node.Add("m_" + key + "Parameter", Name(hash)); }
                node.Add("m_Motion", Motion(source, motionSet)); node.Add("m_Tag", Name(source.m_TagID));
                Document(ClassIDType.AnimatorState, stateIDs[index], node);
            }
        }
        foreach (var t in compiled.m_AnyStateTransitionConstantArray)
        { var id = Transition(t.m_DestinationState, t.m_ConditionConstantArray, t); if (id != 0) root.Any.Add(Ref(id)); }
        foreach (var machine in byHash.Values)
        {
            var node = ObjectNode(machine.Path.Split('.').Last()); node.Add("serializedVersion", 6);
            var childStates = new YAMLSequenceNode(); var i = 0;
            foreach (var index in machine.States)
            {
                var child = new YAMLMappingNode(); child.Add("serializedVersion", 1); child.Add("m_State", Ref(stateIDs[index]));
                child.Add("m_Position", Position(280 + i % 4 * 240, 80 + i / 4 * 100)); childStates.Add(child); i++;
            }
            var childMachines = new YAMLSequenceNode();
            foreach (var child in machine.Children)
            {
                var n = new YAMLMappingNode(); n.Add("serializedVersion", 1); n.Add("m_StateMachine", Ref(child.ID));
                n.Add("m_Position", Position(280 + i % 4 * 240, 80 + i / 4 * 100)); childMachines.Add(n); i++;
            }
            node.Add("m_ChildStates", childStates); node.Add("m_ChildStateMachines", childMachines);
            node.Add("m_AnyStateTransitions", machine.Any); node.Add("m_EntryTransitions", machine.Entries);
            node.Add("m_StateMachineTransitions", machine.Exits); node.Add("m_StateMachineBehaviours", new YAMLSequenceNode());
            node.Add("m_AnyStatePosition", Position(40, 20)); node.Add("m_EntryPosition", Position(40, 100));
            node.Add("m_ExitPosition", Position(40, 180)); node.Add("m_ParentStateMachinePosition", Position(40, 260));
            node.Add("m_DefaultState", Ref(machine.Default)); Document(ClassIDType.AnimatorStateMachine, machine.ID, node);
        }
        return root.ID;
    }

    private YAMLMappingNode Motion(StateConstant state, int motionSet)
    {
        if (state.m_BlendTreeConstantArray.Count == 0) return Ref(0);
        var treeIndex = state.m_BlendTreeConstantIndexArray.Length > 0 ? At(state.m_BlendTreeConstantIndexArray, (uint)motionSet) : motionSet;
        if (treeIndex < 0) return Ref(0);
        if (treeIndex >= state.m_BlendTreeConstantArray.Count) throw new InvalidDataException("状态的运动集索引越界。");
        var tree = state.m_BlendTreeConstantArray[treeIndex]; var active = new HashSet<int>(); var refs = new Dictionary<int, YAMLMappingNode>();
        YAMLMappingNode Visit(int index)
        {
            if (index < 0 || index >= tree.m_NodeArray.Count) throw new InvalidDataException("混合树子节点索引越界。");
            if (active.Contains(index)) throw new InvalidDataException("混合树包含循环。");
            if (refs.TryGetValue(index, out var existing)) return existing;
            var source = tree.m_NodeArray[index];
            if (source.m_ChildIndices.Length == 0)
            {
                if (source.m_ClipID == uint.MaxValue) return Ref(0);
                var clipIndex = checked((int)source.m_ClipID);
                if (state.m_LeafInfoArray != null)
                {
                    clipIndex = -1;
                    foreach (var leaf in state.m_LeafInfoArray)
                    { var found = Array.IndexOf(leaf.m_IDArray, source.m_ClipID); if (found >= 0) { clipIndex = checked((int)leaf.m_IndexOffset + found); break; } }
                }
                if (clipIndex < 0) throw new InvalidDataException("无法定位旧版混合树动画索引。");
                return clip(clipIndex);
            }
            if (source.m_BlendType > 4) throw new NotSupportedException("不支持的混合树类型：" + source.m_BlendType);
            active.Add(index); var id = nextID++; var reference = Ref(id); refs.Add(index, reference); trees++;
            var node = ObjectNode("Blend Tree"); var children = new YAMLSequenceNode();
            for (var i = 0; i < source.m_ChildIndices.Length; i++)
            {
                var childIndex = checked((int)source.m_ChildIndices[i]); var motion = Visit(childIndex);
                var childSource = tree.m_NodeArray[childIndex]; var child = new YAMLMappingNode(); child.Add("serializedVersion", 2); child.Add("m_Motion", motion);
                child.Add("m_Threshold", source.m_BlendType == 0 ? At(source.m_Blend1dData?.m_ChildThresholdArray ?? source.m_ChildThresholdArray, (uint)i) : 0f);
                var position = source.m_BlendType is 1 or 2 or 3 ? At(source.m_Blend2dData.m_ChildPositionArray, (uint)i) : default;
                var xy = new YAMLMappingNode(MappingStyle.Flow); xy.Add("x", position.X); xy.Add("y", position.Y); child.Add("m_Position", xy);
                child.Add("m_TimeScale", childSource.m_ChildIndices.Length > 0 ? 1f : childSource.m_Duration > 0 ? 1f / childSource.m_Duration : 1f);
                child.Add("m_CycleOffset", childSource.m_CycleOffset);
                child.Add("m_DirectBlendParameter", source.m_BlendType == 4 ? Name(At(source.m_BlendDirectData.m_ChildBlendEventIDArray, (uint)i)) : "");
                child.Add("m_Mirror", childSource.m_ChildIndices.Length == 0 && childSource.m_Mirror); children.Add(child);
            }
            active.Remove(index); node.Add("m_Childs", children); node.Add("m_BlendParameter", Name(source.m_BlendEventID)); node.Add("m_BlendParameterY", Name(source.m_BlendEventYID));
            var thresholds = source.m_Blend1dData?.m_ChildThresholdArray ?? source.m_ChildThresholdArray ?? [];
            node.Add("m_MinThreshold", thresholds.Length > 0 ? thresholds.Min() : 0f); node.Add("m_MaxThreshold", thresholds.Length > 0 ? thresholds.Max() : 1f);
            node.Add("m_UseAutomaticThresholds", false); node.Add("m_NormalizedBlendValues", source.m_BlendDirectData?.m_NormalizedBlendValues ?? false);
            node.Add("m_BlendType", source.m_BlendType); Document(ClassIDType.BlendTree, id, node); return reference;
        }
        return tree.m_NodeArray.Count == 0 ? Ref(0) : Visit(0);
    }

    private YAMLSequenceNode Conditions(IEnumerable<ConditionConstant> source)
    {
        var result = new YAMLSequenceNode();
        foreach (var c in source)
        {
            if (c.m_ConditionMode == 5) continue;
            var mode = c.m_ConditionMode; var threshold = c.m_EventThreshold;
            if (mode == 9 && controller.assetsFile.game.Type is GameType.ZZZ or GameType.ZZZ_CB2)
            {
                var parameter = controller.m_Controller.m_Values.m_ValueArray.SingleOrDefault(v => v.m_ID == c.m_EventID);
                if (parameter?.m_Type != 3 || !float.IsFinite(threshold) || Math.Abs(threshold) >= 16777216)
                    throw new NotSupportedException("ZZZ 条件 9 需要可精确转换的整数参数。");
                // ZZZ's inclusive integer lower bound: n >= t is n > ceil(t)-1.
                mode = 3; threshold = MathF.Ceiling(threshold) - 1;
                warnings.Add("ZZZ 整数条件 9 已转换为标准 Unity 的包含下限判断；FrameCount 等游戏脚本参数需要运行时代码更新。");
            }
            if (mode is not (1 or 2 or 3 or 4 or 6 or 7)) throw new NotSupportedException("不支持的转换条件：" + mode);
            var node = new YAMLMappingNode(); node.Add("m_ConditionMode", mode); node.Add("m_ConditionEvent", Name(c.m_EventID));
            node.Add("m_EventTreshold", threshold); result.Add(node);
        }
        return result;
    }
    private string Name(uint hash)
    {
        if (hash is 0 or uint.MaxValue) return "";
        if (controller.m_TOS.TryGetValue(hash, out var name)) return name;
        warnings.Add($"字符串哈希 {hash} 缺少原始名称，使用 hash_{hash}。"); return "hash_" + hash;
    }
    private static T At<T>(T[] values, uint index) => values != null && index < values.Length ? values[index] : throw new InvalidDataException("编译参数或混合树数组索引越界。");
    private static string Parent(string path) => path.LastIndexOf('.') is var i && i >= 0 ? path[..i] : "";
    private static uint Hash(string value) => SevenZip.CRC.CalculateDigestUTF8(value);
    public static YAMLMappingNode Ref(long id, string guid = null)
    {
        var node = new YAMLMappingNode(MappingStyle.Flow); node.Add("fileID", id);
        if (guid != null) { node.Add("guid", guid); node.Add("type", 2); } return node;
    }
    private static YAMLMappingNode Position(float x, float y)
    { var node = new YAMLMappingNode(MappingStyle.Flow); node.Add("x", x); node.Add("y", y); node.Add("z", 0); return node; }
    internal static YAMLMappingNode ObjectNode(string name, bool hidden = true)
    {
        var node = new YAMLMappingNode(); node.Add("m_ObjectHideFlags", hidden ? 1 : 0);
        node.Add("m_CorrespondingSourceObject", Ref(0)); node.Add("m_PrefabInstance", Ref(0)); node.Add("m_PrefabAsset", Ref(0)); node.Add("m_Name", name); return node;
    }
    private void Document(ClassIDType type, long id, YAMLMappingNode node)
    {
        var document = new YAMLDocument(); var root = document.CreateMappingRoot(); root.Tag = ((int)type).ToString(CultureInfo.InvariantCulture);
        root.Anchor = id.ToString(CultureInfo.InvariantCulture); root.Add(type.ToString(), node); writer.AddDocument(document);
    }
}
