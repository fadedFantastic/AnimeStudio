using AnimeStudio;

internal static class FbxTimeSpanTests
{
    public static void Run(string output, Action<bool, string> check)
    {
        var fixture = new AnimationFbxFixture();
        fixture.AnimationList.Clear();
        var path = fixture.RootFrame[0][0].Path;
        ImportedKeyframedAnimation Clip(string name, float first, float last, double? start = null, double? stop = null) =>
            new() { Name = name, SampleRate = 60, StartTime = start, StopTime = stop,
                TrackList = [new() { Path = path, Translations = [new(first, Vector3.Zero), new(last, Vector3.One)] }] };
        fixture.AnimationList.Add(Clip("Attack", 0, 161 / 60f, 0, 161 / 60f));
        fixture.AnimationList.Add(Clip("Attack_End", 0, 187 / 60f, 0, 187 / 60f));
        fixture.AnimationList.Add(Clip("Attack_Default", 0, 161 / 60f, 0, 161 / 60f));
        fixture.AnimationList.Add(Clip("Attack_Default_End", 0, 187 / 60f, 0, 187 / 60f));
        fixture.AnimationList.Add(Clip("AuthoredTail", 0.5f, 1, 0.5, 2.5));
        fixture.AnimationList.Add(Clip("LegacyFallback", 20 / 60f, 80 / 60f));
        fixture.AnimationList.Add(Clip("TerminalSample", 0, 161 / 60f, 0, 160 / 60f));
        fixture.AnimationList.Add(Clip("FloatFrameBoundary", 0, 61 / 60f));
        var expected = new Dictionary<string, (int Start, int Stop)>
        {
            ["Attack"] = (0, 161), ["Attack_End"] = (0, 187), ["Attack_Default"] = (0, 161), ["Attack_Default_End"] = (0, 187),
            ["AuthoredTail"] = (30, 150), ["LegacyFallback"] = (20, 80), ["TerminalSample"] = (0, 161), ["FloatFrameBoundary"] = (0, 61)
        };
        foreach (var compact in new[] { false, true })
        {
            var file = System.IO.Path.Combine(output, "clip-time-ranges-" + compact + ".fbx");
            Fbx.Exporter.Export(file, fixture, new Fbx.ExportOptions { exportAllNodes = true, exportSkins = true,
                exportAnimations = true, preserveRootNodeAsNull = true, castToBone = true, optimizeAnimationSize = compact,
                boneSize = 10, scaleFactor = 1, fbxVersion = 3 });
            var result = FbxInspection.Read(file);
            foreach (var pair in expected)
            {
                var take = result.TakeTimes[pair.Key]; var stack = result.StackTimes[pair.Key];
                check(Math.Abs(take.Start / 46186158000d * 60 - pair.Value.Start) < 0.001 &&
                    Math.Abs(take.Stop / 46186158000d * 60 - pair.Value.Stop) < 0.001,
                    $"per-clip start/end range: {pair.Key}, compact={compact}");
                check(take.Stop >= (long)Math.Round(pair.Value.Stop / 60d * 46186158000d), "end metadata does not fall below the last integer frame: " + pair.Key);
                check(stack["LocalStart"] == take.Start && stack["LocalStop"] == take.Stop &&
                    stack["ReferenceStart"] == take.Start && stack["ReferenceStop"] == take.Stop,
                    "AnimationStack time range agrees with Take: " + pair.Key);
            }
        }
    }
}
