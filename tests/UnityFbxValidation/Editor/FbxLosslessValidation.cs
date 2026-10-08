using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Runs in a disposable Unity project with -testModel, -testBaseline, -testOptimized and -testReport.
public static class FbxLosslessValidation
{
    [Serializable] public sealed class ClipReport
    {
        public string name;
        public int samples, curves, poseDifferences, curveDifferences, meshDifferences;
    }
    [Serializable] public sealed class Report
    {
        public bool success;
        public string error;
        public List<ClipReport> clips = new List<ClipReport>();
    }
    private static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs(); var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException(name);
        return args[index + 1];
    }
    private static string BindingName(EditorCurveBinding b) { return b.path + "|" + b.type.FullName + "|" + b.propertyName; }
    private static AnimationClip[] Import(string path, Avatar avatar)
    {
        var importer = (ModelImporter)AssetImporter.GetAtPath(path);
        importer.animationType = ModelImporterAnimationType.Generic;
        importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
        importer.sourceAvatar = avatar; importer.preserveHierarchy = false; importer.importAnimation = true;
        importer.resampleCurves = true; importer.animationCompression = ModelImporterAnimationCompression.Off;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")).OrderBy(c => c.name).ToArray();
    }
    public static void Run()
    {
        var report = new Report();
        try
        {
            var modelPath = Arg("-testModel");
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.preserveHierarchy = false; importer.SaveAndReimport();
            var avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().Single();
            var baseline = Import(Arg("-testBaseline"), avatar);
            var optimized = Import(Arg("-testOptimized"), avatar);
            if (!baseline.Select(c => c.name).SequenceEqual(optimized.Select(c => c.name))) throw new Exception("Clip names changed");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                for (int c = 0; c < baseline.Length; c++)
                {
                    var a = baseline[c]; var b = optimized[c];
                    if (a.length != b.length || a.frameRate != b.frameRate) throw new Exception("Timing changed: " + a.name);
                    var ab = AnimationUtility.GetCurveBindings(a).OrderBy(BindingName).ToArray();
                    var bb = AnimationUtility.GetCurveBindings(b).OrderBy(BindingName).ToArray();
                    if (!ab.Select(BindingName).SequenceEqual(bb.Select(BindingName))) throw new Exception("Curve bindings changed: " + a.name);
                    var ac = ab.Select(x => AnimationUtility.GetEditorCurve(a, x)).ToArray();
                    var bc = bb.Select(x => AnimationUtility.GetEditorCurve(b, x)).ToArray();
                    var left = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
                    var right = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
                    try
                    {
                        var lt = left.GetComponentsInChildren<Transform>(true).OrderBy(t => AnimationUtility.CalculateTransformPath(t, left.transform)).ToArray();
                        var rt = right.GetComponentsInChildren<Transform>(true).OrderBy(t => AnimationUtility.CalculateTransformPath(t, right.transform)).ToArray();
                        var lm = left.GetComponentsInChildren<SkinnedMeshRenderer>(true).OrderBy(x => x.name).ToArray();
                        var rm = right.GetComponentsInChildren<SkinnedMeshRenderer>(true).OrderBy(x => x.name).ToArray();
                        var row = new ClipReport { name = a.name, curves = ab.Length }; report.clips.Add(row);
                        int steps = Mathf.CeilToInt(a.length * a.frameRate * 2); // Authored frames and half frames.
                        for (int sample = 0; sample <= steps; sample++)
                        {
                            float time = Mathf.Min(sample / (a.frameRate * 2), a.length);
                            row.samples++;
                            for (int i = 0; i < ac.Length; i++) if (!ac[i].Evaluate(time).Equals(bc[i].Evaluate(time))) row.curveDifferences++;
                            a.SampleAnimation(left, time); b.SampleAnimation(right, time);
                            for (int i = 0; i < lt.Length; i++)
                                if (!lt[i].localPosition.Equals(rt[i].localPosition) || !lt[i].localRotation.Equals(rt[i].localRotation) || !lt[i].localScale.Equals(rt[i].localScale)) row.poseDifferences++;
                            for (int i = 0; i < lm.Length; i++)
                                for (int shape = 0; shape < lm[i].sharedMesh.blendShapeCount; shape++)
                                    if (lm[i].GetBlendShapeWeight(shape) != rm[i].GetBlendShapeWeight(shape)) row.poseDifferences++;
                            if (sample == 0 || sample == steps / 2 || sample == steps)
                                for (int i = 0; i < lm.Length; i++)
                                {
                                    var am = new Mesh(); var bm = new Mesh();
                                    try
                                    {
                                        lm[i].BakeMesh(am); rm[i].BakeMesh(bm);
                                        var av = am.vertices; var bv = bm.vertices;
                                        if (av.Length != bv.Length) throw new Exception("Mesh topology changed");
                                        for (int v = 0; v < av.Length; v++) if (!av[v].Equals(bv[v])) row.meshDifferences++;
                                    }
                                    finally { UnityEngine.Object.DestroyImmediate(am); UnityEngine.Object.DestroyImmediate(bm); }
                                }
                        }
                    }
                    finally { UnityEngine.Object.DestroyImmediate(left); UnityEngine.Object.DestroyImmediate(right); }
                }
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
            report.success = report.clips.Count > 0 && report.clips.All(c => c.curveDifferences == 0 && c.poseDifferences == 0 && c.meshDifferences == 0);
            if (!report.success) throw new Exception("Animation differs; no error tolerance is allowed.");
        }
        catch (Exception ex) { report.success = false; report.error = ex.ToString(); }
        File.WriteAllText(Arg("-testReport"), JsonUtility.ToJson(report, true));
        if (!report.success) throw new Exception(report.error);
    }
}
