using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class ControllerValidation
{
    [Serializable] public class Suite { public Expected[] controllers; }
    [Serializable] public class Expected
    {
        public string path;
        public State[] states;
        public Layer[] layers;
        public Parameter[] parameters;
        public int machines, transitions, blendTrees, animations;
    }
    [Serializable] public class State
    {
        public string path, tag, speedParameter, timeParameter;
        public float speed, cycleOffset;
        public bool ikOnFeet, writeDefaults, mirror, hasMotion;
        public Transition[] transitions;
    }
    [Serializable] public class Transition
    {
        public string destination;
        public bool isMachine, isExit, hasExitTime, fixedDuration, ordered, canSelf;
        public float duration, offset, exitTime;
        public int interruption;
        public Condition[] conditions;
    }
    [Serializable] public class Condition { public int mode; public string parameter; public float threshold; }
    [Serializable] public class Layer { public string name; public int blendingMode; public float defaultWeight; public bool ikPass, affectsTiming; }
    [Serializable] public class Parameter { public string name; public int type, intValue; public float floatValue; public bool boolValue; }
    [Serializable] public class Measurement
    {
        public string path;
        public int states, machines, transitions, blendTrees, parameters, animations, curveBindings;
        public bool editAndReload;
    }
    [Serializable] public class Report { public bool success; public string error; public List<Measurement> controllers = new List<Measurement>(); }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Close(float a, float b, string name) { Check(Math.Abs(a - b) < 0.000001f, name + ": " + a + " != " + b); }

    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        var reportPath = args[Array.IndexOf(args, "-testReport") + 1];
        var expectations = args[Array.IndexOf(args, "-expectationsPath") + 1];
        var report = new Report();
        try
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var suite = JsonUtility.FromJson<Suite>(File.ReadAllText(expectations));
            Check(suite.controllers.Length == 4, "expected four real controllers");
            foreach (var expected in suite.controllers)
            {
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(expected.path);
                Check(controller != null, "Unity cannot import controller: " + expected.path);
                var assets = AssetDatabase.LoadAllAssetsAtPath(expected.path);
                var m = new Measurement { path = expected.path,
                    states = assets.OfType<AnimatorState>().Count(), machines = assets.OfType<AnimatorStateMachine>().Count(),
                    transitions = assets.OfType<AnimatorTransitionBase>().Count(), blendTrees = assets.OfType<BlendTree>().Count(),
                    parameters = controller.parameters.Length, animations = controller.animationClips.Length };
                Check(m.states == expected.states.Length && m.machines == expected.machines && m.transitions == expected.transitions &&
                    m.blendTrees == expected.blendTrees, "imported graph object counts differ: " + expected.path);
                Check(m.parameters == expected.parameters.Length && controller.layers.Length == expected.layers.Length,
                    "parameter/layer count changed during import");
                for (var i = 0; i < expected.parameters.Length; i++)
                {
                    var p = expected.parameters[i]; var actual = controller.parameters[i];
                    Check(actual.name == p.name && (int)actual.type == p.type && actual.defaultInt == p.intValue && actual.defaultBool == p.boolValue,
                        "parameter changed: " + p.name); Close(actual.defaultFloat, p.floatValue, p.name);
                }
                var statePaths = new Dictionary<AnimatorState, string>();
                var machinePaths = new Dictionary<AnimatorStateMachine, string>();
                Action<AnimatorStateMachine, string> visit = null;
                visit = (machine, path) =>
                {
                    machinePaths.Add(machine, path);
                    foreach (var child in machine.states) statePaths.Add(child.state, path + "." + child.state.name);
                    foreach (var child in machine.stateMachines) visit(child.stateMachine, path + "." + child.stateMachine.name);
                };
                for (var i = 0; i < expected.layers.Length; i++)
                {
                    var l = expected.layers[i]; var actual = controller.layers[i];
                    Check(actual.name == l.name && (int)actual.blendingMode == l.blendingMode && actual.iKPass == l.ikPass && actual.syncedLayerAffectsTiming == l.affectsTiming,
                        "layer changed: " + l.name); Close(actual.defaultWeight, l.defaultWeight, l.name);
                    visit(actual.stateMachine, actual.name);
                }
                foreach (var e in expected.states)
                {
                    var s = statePaths.Single(p => p.Value == e.path).Key;
                    Close(s.speed, e.speed, e.path + " speed"); Close(s.cycleOffset, e.cycleOffset, e.path + " cycle offset");
                    Check(s.tag == e.tag && s.iKOnFeet == e.ikOnFeet && s.writeDefaultValues == e.writeDefaults && s.mirror == e.mirror &&
                        s.speedParameter == e.speedParameter && s.timeParameter == e.timeParameter, "state fields changed: " + e.path);
                    Check((s.motion != null) == e.hasMotion, "motion reference missing: " + e.path);
                    Check(s.transitions.Length == e.transitions.Length, "transition count changed: " + e.path);
                    for (var i = 0; i < e.transitions.Length; i++)
                    {
                        var t = s.transitions[i]; var source = e.transitions[i];
                        Check(t.isExit == source.isExit && t.hasExitTime == source.hasExitTime && t.hasFixedDuration == source.fixedDuration &&
                            t.orderedInterruption == source.ordered && (int)t.interruptionSource == source.interruption && t.canTransitionToSelf == source.canSelf,
                            "transition flags changed: " + e.path);
                        Close(t.duration, source.duration, "duration"); Close(t.offset, source.offset, "offset"); Close(t.exitTime, source.exitTime, "exit time");
                        if (!source.isExit)
                            Check(source.isMachine ? t.destinationStateMachine != null && machinePaths[t.destinationStateMachine] == source.destination
                                : t.destinationState != null && statePaths[t.destinationState] == source.destination, "transition destination changed: " + e.path);
                        Check(t.conditions.Length == source.conditions.Length, "transition conditions changed");
                        for (var j = 0; j < source.conditions.Length; j++)
                        {
                            Check((int)t.conditions[j].mode == source.conditions[j].mode && t.conditions[j].parameter == source.conditions[j].parameter,
                                "condition mode/parameter changed"); Close(t.conditions[j].threshold, source.conditions[j].threshold, "condition threshold");
                        }
                    }
                }
                // Unity's animationClips includes clips reachable through nested BlendTrees and
                // may report additional de-duplicated references after import. The restored
                // controller must retain every source reference, so only reject a loss.
                Check(m.animations >= expected.animations, "Unity animation references lost: " + expected.path +
                    " (actual=" + m.animations + ", expected minimum=" + expected.animations + ")");
                foreach (var clip in controller.animationClips)
                    m.curveBindings += AnimationUtility.GetCurveBindings(clip).Length + AnimationUtility.GetObjectReferenceCurveBindings(clip).Length;
                Check(m.curveBindings > 0, "controller animations have no imported curves");
                const string marker = "AnimeStudioValidationFlag";
                controller.AddParameter(marker, AnimatorControllerParameterType.Bool);
                EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(expected.path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(expected.path);
                Check(controller.parameters.Any(p => p.name == marker), "controller cannot be edited and saved");
                controller.RemoveParameter(Array.FindIndex(controller.parameters, p => p.name == marker));
                EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
                m.editAndReload = true; report.controllers.Add(m);
            }
            report.success = true;
        }
        catch (Exception ex) { report.error = ex.ToString(); Debug.LogException(ex); }
        File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
        EditorApplication.Exit(report.success ? 0 : 1);
    }
}
