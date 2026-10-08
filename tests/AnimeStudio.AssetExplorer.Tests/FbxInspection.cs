using System.Text;
using AnimeStudio;

// Inspect actual native output, including the root attribute that determines Unity's binding paths.
internal sealed class FbxInspection
{
    public sealed record Node(long Id, string Name, string Type);
    public List<Node> Models { get; } = [];
    public List<string> Animations { get; } = [];
    public int CurveCount { get; private set; }
    public List<long> CurveIds { get; } = [];
    public List<(long Child, long Parent)> Connections { get; } = [];
    public Node[] Roots => Models.Where(m => Connections.Any(c => c.Child == m.Id && c.Parent == 0)).ToArray();

    public static FbxInspection Read(string file)
    {
        using var reader = new BinaryReader(File.OpenRead(file));
        if (!Encoding.ASCII.GetString(reader.ReadBytes(23)).StartsWith("Kaydara FBX Binary")) throw new InvalidDataException("Expected binary FBX");
        var wide = reader.ReadInt32() >= 7500;
        var headerSize = wide ? 25 : 13;
        var result = new FbxInspection();
        bool ReadNode()
        {
            var end = wide ? reader.ReadInt64() : reader.ReadUInt32();
            var count = wide ? reader.ReadInt64() : reader.ReadUInt32();
            var length = wide ? reader.ReadInt64() : reader.ReadUInt32();
            var nameLength = reader.ReadByte();
            if (end == 0) return false;
            var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
            var dataEnd = reader.BaseStream.Position + length;
            var properties = new List<object>();
            if (name is "Model" or "AnimationStack" or "AnimationCurve" or "C")
                for (var i = 0; i < count; i++)
                {
                    var type = reader.ReadChar();
                    properties.Add(type switch
                    {
                        'L' => reader.ReadInt64(), 'I' => reader.ReadInt32(), 'Y' => reader.ReadInt16(),
                        'D' => reader.ReadDouble(), 'F' => reader.ReadSingle(), 'C' => reader.ReadBoolean(),
                        'S' => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())),
                        _ => throw new InvalidDataException("Unexpected FBX property: " + type)
                    });
                }
            reader.BaseStream.Position = dataEnd;
            if (name == "Model") result.Models.Add(new((long)properties[0], ((string)properties[1]).Split('\0')[0], (string)properties[2]));
            if (name == "AnimationStack") result.Animations.Add(((string)properties[1]).Split('\0')[0]);
            if (name == "AnimationCurve") { result.CurveCount++; result.CurveIds.Add((long)properties[0]); }
            if (name == "C" && (string)properties[0] == "OO") result.Connections.Add(((long)properties[1], (long)properties[2]));
            while (reader.BaseStream.Position < end - headerSize) if (!ReadNode()) break;
            reader.BaseStream.Position = end;
            return true;
        }
        while (ReadNode()) { }
        return result;
    }
}

internal sealed class AnimationFbxFixture : IImported
{
    public ImportedFrame RootFrame { get; } = Frame("AnimationContainer");
    public List<ImportedMesh> MeshList { get; } = [];
    public List<ImportedMaterial> MaterialList { get; } = [];
    public List<ImportedTexture> TextureList { get; } = [];
    public List<ImportedKeyframedAnimation> AnimationList { get; } = [];
    public List<ImportedMorph> MorphList { get; } = [];

    private static ImportedFrame Frame(string name) => new() { Name = name, LocalScale = Vector3.One, LocalRotation = new Quaternion(0, 0, 0, 1) };
    public AnimationFbxFixture()
    {
        var boneRoot = Frame("Bone_Root"); var bone = Frame("Bip001");
        RootFrame.AddChild(boneRoot); boneRoot.AddChild(bone);
        AnimationList.Add(new() { Name = "Attack", SampleRate = 60, TrackList = [new() { Path = bone.Path,
            Translations = [new(0, Vector3.Zero), new(1, new Vector3(1, 0, 0))] }] });
    }
}
