using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Copy this script and the model/animation pair into a disposable Unity test project.
// Run -batchmode -executeMethod FbxPlaybackValidation.Run -quit with
// -testModel Assets/Model.fbx -testAnimation Assets/Animation.fbx -testReport <absolute-json>.
public static class FbxPlaybackValidation
{
    [Serializable] public sealed class ClipResult
    {
        public string name;
        public float seconds;
        public float firstFrame, lastFrame, frameRate;
        public int paths, matchedPaths, movedTransforms;
    }
    [Serializable] public sealed class Report
    {
        public bool success;
        public string error;
        public bool avatarValid;
        public int renderers;
        public List<ClipResult> clips = new List<ClipResult>();
    }

    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing " + name);
        return args[index + 1];
    }

    public static void Run()
    {
        var report = new Report();
        var reportPath = Argument("-testReport");
        try
        {
            var modelPath = Argument("-testModel");
            var animationPath = Argument("-testAnimation");
            var modelImporter = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            modelImporter.animationType = ModelImporterAnimationType.Generic;
            modelImporter.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            modelImporter.preserveHierarchy = false;
            modelImporter.SaveAndReimport();
            var avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().Single();
            report.avatarValid = avatar.isValid;
            var animationImporter = (ModelImporter)AssetImporter.GetAtPath(animationPath);
            animationImporter.animationType = ModelImporterAnimationType.Generic;
            animationImporter.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            animationImporter.sourceAvatar = avatar;
            animationImporter.preserveHierarchy = false;
            animationImporter.importAnimation = true;
            animationImporter.SaveAndReimport();
            var takeRanges = animationImporter.defaultClipAnimations.ToDictionary(c => c.name);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            report.renderers = model.GetComponentsInChildren<Renderer>(true).Length;
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(animationPath).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")))
                {
                    var paths = AnimationUtility.GetCurveBindings(clip).Select(b => b.path).Distinct().ToArray();
                    var range = takeRanges[clip.name];
                    var row = new ClipResult { name = clip.name, seconds = clip.length, frameRate = clip.frameRate,
                        firstFrame = range.firstFrame, lastFrame = range.lastFrame, paths = paths.Length,
                        matchedPaths = paths.Count(p => p.Length == 0 || model.transform.Find(p) != null) };
                    report.clips.Add(row);
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
                    try
                    {
                        var transforms = instance.GetComponentsInChildren<Transform>(true);
                        clip.SampleAnimation(instance, 0);
                        var positions = transforms.Select(t => t.localPosition).ToArray();
                        var rotations = transforms.Select(t => t.localRotation).ToArray();
                        var scales = transforms.Select(t => t.localScale).ToArray();
                        clip.SampleAnimation(instance, clip.length * 0.37f);
                        for (var i = 0; i < transforms.Length; i++)
                            if (Vector3.Distance(positions[i], transforms[i].localPosition) > 0.00001f ||
                                Quaternion.Angle(rotations[i], transforms[i].localRotation) > 0.01f ||
                                Vector3.Distance(scales[i], transforms[i].localScale) > 0.00001f) row.movedTransforms++;
                    }
                    finally { UnityEngine.Object.DestroyImmediate(instance); }
                }
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
            report.success = report.avatarValid && report.renderers > 0 && report.clips.Count > 0 &&
                report.clips.All(c => c.seconds > 0 && c.paths > 0 && c.paths == c.matchedPaths &&
                    Math.Abs(c.lastFrame - c.firstFrame - c.seconds * c.frameRate) < 0.001f) && report.clips.Any(c => c.movedTransforms > 10);
            if (!report.success) throw new InvalidDataException("FBX animation cannot fully bind to and animate the model; inspect the report.");
        }
        catch (Exception ex) { report.error = ex.ToString(); report.success = false; }
        File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
        if (!report.success) throw new InvalidOperationException(report.error);
    }
}
