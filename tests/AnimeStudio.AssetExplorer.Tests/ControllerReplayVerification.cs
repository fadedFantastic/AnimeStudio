using System.Globalization;
using System.Security.Cryptography;
using AnimeStudio;
using AnimeStudio.AssetExplorer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class ControllerReplayVerification
{
    public static void Run(string files, string output, Action<bool, string> check)
    {
        Logger.Silent = true; files = Path.GetFullPath(files);
        var expectations = new List<object>();
        foreach (var report in Directory.GetFiles(files, "*.controller.restore.json", SearchOption.AllDirectories))
        {
            var restoration = JObject.Parse(File.ReadAllText(report)); var editor = report[..^".restore.json".Length];
            var raw = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(editor), (string)restoration["OriginalData"]));
            var metadata = JObject.Parse(File.ReadAllText(raw + ".info.json"));
            check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(raw))) == (string)metadata["MainSHA256"] &&
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(raw + ".cab"))) == (string)metadata["CabSHA256"],
                "exported research files match checksums: " + Path.GetFileName(editor));
            var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ZZZ), SkipProcess = true, ResolveDependencies = false,
                FilterData = new() { Items = [new() { Source = (string)metadata["Source"], Offset = (long)metadata["BundleOffset"] }] } };
            try
            {
                manager.LoadFiles([(string)metadata["Source"]], mergeSplitAssets: false);
                var file = manager.assetsFileList.Single(f => f.fileName == (string)metadata["Cab"]);
                var info = file.m_Objects.Single(i => i.m_PathID.ToString(CultureInfo.InvariantCulture) == (string)metadata["PathID"]);
                file.reader.Position = info.byteStart;
                check(file.reader.ReadBytes((int)info.byteSize).SequenceEqual(File.ReadAllBytes(raw)), "raw object equals independently reloaded game source");
                file.reader.Position = 0;
                check(file.reader.ReadBytes(checked((int)file.header.m_FileSize)).SequenceEqual(File.ReadAllBytes(raw + ".cab")),
                    "research CAB equals complete original serialized file");
                var controller = new AnimatorController(new ObjectReader(file.reader, file, info, manager.Game));
                string Name(uint hash) => hash is 0 or uint.MaxValue ? "" : controller.m_TOS.GetValueOrDefault(hash, "hash_" + hash);
                var graph = controller.m_Controller;
                var sourceStates = new List<object>(); var sourceLayers = new List<object>();
                foreach (var layer in graph.m_LayerArray)
                {
                    sourceLayers.Add(new { name = Name(layer.m_Binding), blendingMode = layer.m_LayerBlendingMode, defaultWeight = layer.m_DefaultWeight,
                        ikPass = layer.m_IKPass, affectsTiming = layer.m_SyncedLayerAffectsTiming });
                    var machine = graph.m_StateMachineArray[(int)layer.m_StateMachineIndex];
                    var machinePaths = new Dictionary<uint, string> { [layer.m_Binding] = Name(layer.m_Binding) };
                    foreach (var state in machine.m_StateConstantArray)
                    {
                        var path = Name(state.m_FullPathID);
                        while (path.Contains('.'))
                        { path = path[..path.LastIndexOf('.')]; machinePaths[SevenZip.CRC.CalculateDigestUTF8(path)] = path; }
                    }
                    foreach (var state in machine.m_StateConstantArray)
                    {
                        sourceStates.Add(new
                        {
                            path = Name(state.m_FullPathID), speed = state.m_Speed, cycleOffset = state.m_CycleOffset, tag = Name(state.m_TagID),
                            ikOnFeet = state.m_IKOnFeet, writeDefaults = state.m_WriteDefaultValues, mirror = state.m_Mirror,
                            speedParameter = Name(state.m_SpeedParamID), timeParameter = Name(state.m_TimeParamID),
                            hasMotion = state.m_BlendTreeConstantArray.Any(t => t.m_NodeArray.Count > 0),
                            transitions = state.m_TransitionConstantArray.Where(t => t.m_DestinationState != uint.MaxValue).Select(t =>
                            {
                                var selector = t.m_DestinationState >= 30000 ? machine.m_SelectorStateConstantArray[(int)(t.m_DestinationState % 30000)] : null;
                                var destination = selector == null ? Name(machine.m_StateConstantArray[(int)t.m_DestinationState].m_FullPathID) :
                                    machinePaths.GetValueOrDefault(selector.m_FullPathID, Name(layer.m_Binding) + ".Recovered_" + selector.m_FullPathID);
                                return new
                                {
                                    destination, isMachine = selector?.m_isEntry == true, isExit = selector?.m_isEntry == false,
                                    duration = t.m_TransitionDuration, offset = t.m_TransitionOffset, exitTime = t.m_ExitTime,
                                    hasExitTime = t.m_HasExitTime, fixedDuration = t.m_HasFixedDuration,
                                    interruption = t.m_InterruptionSource, ordered = t.m_OrderedInterruption, canSelf = t.m_CanTransitionToSelf,
                                    conditions = t.m_ConditionConstantArray.Where(c => c.m_ConditionMode != 5).Select(c => new
                                    { mode = c.m_ConditionMode == 9 ? 3 : c.m_ConditionMode, parameter = Name(c.m_EventID), threshold = c.m_ConditionMode == 9 ? MathF.Ceiling(c.m_EventThreshold) - 1 : c.m_EventThreshold })
                                };
                            })
                        });
                    }
                }
                expectations.Add(new
                {
                    path = "Assets/Controllers/" + Path.GetRelativePath(files, editor).Replace('\\', '/'),
                    states = sourceStates, layers = sourceLayers,
                    machines = (int)restoration["StateMachines"], transitions = (int)restoration["Transitions"], blendTrees = (int)restoration["BlendTrees"],
                    animations = restoration["Animations"].Count(),
                    parameters = graph.m_Values.m_ValueArray.Select(v => new
                    {
                        name = Name(v.m_ID), type = (int)v.m_Type,
                        floatValue = v.m_Type == 1 ? graph.m_DefaultValues.m_FloatValues[v.m_Index] : 0,
                        intValue = v.m_Type == 3 ? graph.m_DefaultValues.m_IntValues[v.m_Index] : 0,
                        boolValue = v.m_Type is 4 or 9 && graph.m_DefaultValues.m_BoolValues[v.m_Index]
                    })
                });
            }
            finally { manager.Clear(); }
        }
        check(expectations.Count == 4, "all four original controller resources have editable and research exports");
        File.WriteAllText(Path.Combine(output, "expectations.json"), JsonConvert.SerializeObject(new { controllers = expectations }, Formatting.Indented));
    }
}
