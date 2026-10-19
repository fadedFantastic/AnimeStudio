using AnimeStudio;

internal static class FbxScaleTests
{
    public static void Run(string output, Action<bool, string> check)
    {
        // A one-meter mesh and a separately exported one-meter translation must
        // use the same unit, also with a user multiplier and animation compaction.
        var fixture = new AnimationFbxFixture();
        var meshFrame = new ImportedFrame { Name = "MeterMesh", LocalScale = Vector3.One,
            LocalRotation = new Quaternion(0, 0, 0, 1) };
        fixture.RootFrame.AddChild(meshFrame);
        fixture.MeshList.Add(new ImportedMesh { Path = meshFrame.Path, hasUV = [], uvType = [],
            VertexList = [new() { Vertex = Vector3.Zero }, new() { Vertex = new Vector3(1, 0, 0) },
                new() { Vertex = new Vector3(0, 1, 0) }],
            SubmeshList = [new() { FaceList = [new() { VertexIndices = [0, 1, 2] }] }] });
        var animated = fixture.AnimationList.ToArray();
        foreach (var multiplier in new[] { 0.01f, 1f, 2f })
        foreach (var animations in new[] { false, true })
        {
            fixture.AnimationList.Clear();
            if (animations) fixture.AnimationList.AddRange(animated);
            var file = Path.Combine(output, $"meter-scale-{multiplier}-{animations}.fbx");
            Fbx.Exporter.Export(file, fixture, new Fbx.ExportOptions { exportAllNodes = true,
                exportSkins = true, exportAnimations = true, preserveRootNodeAsNull = true,
                optimizeAnimationSize = true, boneSize = 10, scaleFactor = multiplier, fbxVersion = 3 });
            var inspection = FbxInspection.Read(file);
            check(Math.Abs(inspection.Units["UnitScaleFactor"] / 100 - multiplier) < 1e-7,
                $"one Unity meter exports as {multiplier} meters, animations={animations}");
        }
        fixture.MeshList.Clear();
        fixture.AnimationList.Clear();
        fixture.AnimationList.AddRange(animated);
        var separate = Path.Combine(output, "meter-separate-animation.fbx");
        Fbx.Exporter.Export(separate, fixture, new Fbx.ExportOptions { exportAllNodes = true,
            exportAnimations = true, preserveRootNodeAsNull = true, castToBone = true, boneSize = 10, fbxVersion = 3 });
        check(FbxInspection.Read(separate).Units["UnitScaleFactor"] == 100,
            "separate animation and default scale use meters just like the model");
    }
}
