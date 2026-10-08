using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AnimeStudio;

internal static class FbxOptimizationTests
{
    public static void Run(string output, Action<bool, string> check)
    {
        var fixture = new AnimationFbxFixture();
        fixture.AnimationList[0].TrackList[0].Scalings = Enumerable.Range(0, 188)
            .Select(i => new ImportedKeyframe<Vector3>(i / 60f, Vector3.One)).ToList();
        var baseline = Path.Combine(output, "lossless-baseline.fbx");
        Fbx.Exporter.Export(baseline, fixture, new Fbx.ExportOptions { exportAllNodes = true, exportSkins = true,
            exportAnimations = true, castToBone = true, preserveRootNodeAsNull = true, boneSize = 10, scaleFactor = 1, fbxVersion = 3 });
        var original = File.ReadAllBytes(baseline);
        var target = Path.Combine(output, "lossless-optimized.fbx");
        File.WriteAllBytes(target, original);
        var result = FbxBinaryOptimizer.Optimize(target);
        check(result.ConstantCurves == 3 && result.RemovedKeys == 3 * 186, "only fully constant curves lose redundant interior keys");
        var before = FbxInspection.Read(baseline); var after = FbxInspection.Read(target);
        check(before.Models.SequenceEqual(after.Models) && before.Animations.SequenceEqual(after.Animations) &&
            before.Connections.SequenceEqual(after.Connections) && before.CurveCount == after.CurveCount,
            "constant and referenced empty curve bindings remain intact");
        check(result.OptimizedBytes < result.OriginalBytes, "native animation file is smaller after lossless compaction");
        var automatic = Path.Combine(output, "lossless-automatic.fbx");
        Fbx.Exporter.Export(automatic, fixture, new Fbx.ExportOptions { exportAllNodes = true, exportSkins = true,
            exportAnimations = true, castToBone = true, preserveRootNodeAsNull = true, optimizeAnimationSize = true,
            boneSize = 10, scaleFactor = 1, fbxVersion = 3 });
        check(new FileInfo(automatic).Length < original.Length && FbxInspection.Read(automatic).Animations.SequenceEqual(before.Animations),
            "normal FBX export applies the opt-in compaction automatically");
        var once = File.ReadAllBytes(target);
        FbxBinaryOptimizer.Optimize(target);
        check(once.SequenceEqual(File.ReadAllBytes(target)), "repeated compaction does not change the file again");

        // Mutate fixed-size native fields without changing the FBX layout. These are deliberately
        // difficult inputs: same values with a nonzero tangent, unknown flags, and a one-ULP change.
        int ArrayData(byte[] data, string name, char type, int count)
        {
            var signature = Encoding.ASCII.GetBytes(name + type);
            var offset = data.AsSpan().IndexOf(signature);
            if (offset < 0) throw new InvalidDataException("Native fixture array not found: " + name);
            offset += signature.Length;
            if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset)) != count ||
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4)) != 0)
                throw new InvalidDataException("Unexpected native fixture array encoding: " + name);
            return offset + 12;
        }
        void Edge(string name, Action<byte[]> change)
        {
            var bytes = (byte[])original.Clone(); change(bytes);
            var file = Path.Combine(output, name + ".fbx"); File.WriteAllBytes(file, bytes);
            var edge = FbxBinaryOptimizer.Optimize(file);
            check(edge.ConstantCurves == 2 && edge.RemovedKeys == 2 * 186, name + " is preserved instead of approximated");
        }
        Edge("constant-values-with-tangent", bytes => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(ArrayData(bytes, "KeyAttrDataFloat", 'f', 4)), 1));
        Edge("unknown-interpolation-flags", bytes => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(ArrayData(bytes, "KeyAttrFlags", 'i', 1)), 0x6108));
        Edge("one-ulp-motion", bytes => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(ArrayData(bytes, "KeyValueFloat", 'f', 188) + 90 * 4), BitConverter.SingleToInt32Bits(1f) + 1));

        foreach (var version in new[] { 7300, 7500 })
        {
            var emptyFile = Path.Combine(output, "empty-curves-" + version + ".fbx");
            WriteEmptyFixture(emptyFile, version);
            var compacted = FbxBinaryOptimizer.Optimize(emptyFile);
            var emptyRead = FbxInspection.Read(emptyFile);
            check(compacted.UnusedEmptyCurves == 1 && emptyRead.CurveIds.Order().SequenceEqual(new long[] { 100, 101 }),
                "only unreferenced empty curves are removed; direct and metadata references survive (" + version + ")");
        }

        var ascii = Path.Combine(output, "legacy-ascii.fbx"); File.WriteAllText(ascii, "; FBX ASCII fixture");
        var hash = SHA256.HashData(File.ReadAllBytes(ascii)); FbxBinaryOptimizer.Optimize(ascii);
        check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(ascii))), "unsupported FBX formats are left intact");
        var broken = Path.Combine(output, "malformed.fbx"); var truncated = original[..(original.Length / 2)]; File.WriteAllBytes(broken, truncated);
        try { FbxBinaryOptimizer.Optimize(broken); throw new Exception("Malformed FBX accepted"); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { }
        check(truncated.SequenceEqual(File.ReadAllBytes(broken)), "failed compaction cannot overwrite the input file");
    }

    private sealed record Stub(string Name, byte[][] Properties, Stub[] Children);
    private static void WriteEmptyFixture(string path, int version)
    {
        byte[] Scalar(char type, Action<BinaryWriter> value)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write((byte)type); value(writer); return stream.ToArray();
        }
        byte[] I(int value) => Scalar('I', w => w.Write(value));
        byte[] L(long value) => Scalar('L', w => w.Write(value));
        byte[] S(string value) => Scalar('S', w => { var bytes = Encoding.UTF8.GetBytes(value); w.Write(bytes.Length); w.Write(bytes); });
        byte[] Empty(char type) => Scalar(type, w => { w.Write(0); w.Write(0); w.Write(0); });
        Stub N(string name, byte[][] props, params Stub[] children) => new(name, props, children);
        Stub Curve(long id) => N("AnimationCurve", [L(id), S("curve\0\u0001AnimCurve"), S("")],
            N("KeyVer", [I(4009)]), N("KeyTime", [Empty('l')]), N("KeyValueFloat", [Empty('f')]));
        var roots = new[]
        {
            N("Definitions", [], N("Count", [I(4)]), N("ObjectType", [S("AnimationCurve")], N("Count", [I(3)]))),
            N("Objects", [], Curve(100), Curve(101), Curve(102), N("AnimationCurveNode", [L(300), S("T"), S("")])),
            N("Connections", [], N("C", [S("OP"), L(100), L(300), S("d|X")])),
            N("CustomMetadata", [L(101)])
        };
        using var output = new BinaryWriter(File.Create(path));
        output.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0")); output.Write(version);
        var header = version >= 7500 ? 25 : 13;
        void Word(long value) { if (version >= 7500) output.Write(value); else output.Write((uint)value); }
        void WriteNode(Stub node)
        {
            var start = output.BaseStream.Position; output.Write(new byte[header]);
            var name = Encoding.ASCII.GetBytes(node.Name); output.Write(name);
            var propsStart = output.BaseStream.Position; foreach (var prop in node.Properties) output.Write(prop);
            var propsLength = output.BaseStream.Position - propsStart;
            foreach (var child in node.Children) WriteNode(child);
            if (node.Children.Length > 0) output.Write(new byte[header]);
            var end = output.BaseStream.Position;
            output.BaseStream.Position = start; Word(end); Word(node.Properties.Length); Word(propsLength); output.Write((byte)name.Length);
            output.BaseStream.Position = end;
        }
        foreach (var root in roots) WriteNode(root);
        output.Write(new byte[header]);
        output.Write(Convert.FromHexString("FABC AA0E D7C8 D760 B47F F685 1DF4 277B".Replace(" ", "")));
        output.Write(new byte[(16 - output.BaseStream.Position % 16) % 16 + 4]);
        output.Write(version); output.Write(new byte[120]);
        output.Write(Convert.FromHexString("F85A8C6ADEF5D97EECE90CE3758F290B"));
    }
}
