using System.Runtime.CompilerServices;
using System.Text;
using AnimeStudio;
using AnimeStudio.AssetExplorer;

internal static class ExportLoadingTests
{
    private const string OldControllerHash = "9860551FBCD452140FD34D9A51C380DE";
    private const string NewControllerHash = "1D32692ACDFBEFAD9FE0C256BA26E95C";

    public static void Run(Action<bool, string> check)
    {
        CheckCopies(check);
        foreach (var (game, typeID, hash) in new[]
        {
            (GameType.ZZZ, true, OldControllerHash), (GameType.ZZZ_CB2, true, OldControllerHash),
            (GameType.ZZZ, false, NewControllerHash), (GameType.ZZZ_CB2, false, NewControllerHash),
            (GameType.Normal, false, NewControllerHash), (GameType.Normal, false, OldControllerHash)
        })
        {
            using var stream = ControllerFixture(typeID);
            using var reader = Reader(stream, game, ClassIDType.AnimatorController, hash);
            var controller = new AnimatorController(reader);
            check(controller.m_TOS[101] == "Base Layer.Idle" && controller.m_TOS[102] == "Base Layer.Walk",
                $"controller defaults stay aligned with the string table: {game}, TypeID={typeID}");
            check(controller.m_AnimationClips.Count == 1 && controller.m_AnimationClips[0].m_FileID == 1 &&
                    controller.m_AnimationClips[0].m_PathID == 123456789 && reader.BytesLeft() == 0,
                $"controller preserves animation references and respects the object boundary: {game}, TypeID={typeID}");
        }

        // ZZZ animation bindings still use the ordinary modern Unity value layout.
        using var clipStream = new MemoryStream();
        using (var writer = new BinaryWriter(clipStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0L); writer.Write(101u); writer.Write(3u); writer.Write(7u);
        }
        using var clipReader = Reader(clipStream, GameType.ZZZ, ClassIDType.AnimationClip, OldControllerHash);
        clipReader.Reset();
        var binding = new ValueConstant(clipReader);
        check(binding.m_ID == 101 && binding.m_Type == 3 && binding.m_Index == 7 && clipReader.BytesLeft() == 0,
            "ZZZ controller compatibility leaves animation binding layouts unchanged");
    }

    private static void CheckCopies(Action<bool, string> check)
    {
        const string path = "Assets/Hero/Hero.prefab";
        CatalogAsset Row(string cab, string source, long offset, long id, ClassIDType type) => new()
        {
            Container = path, Cab = cab, Source = source, Offset = offset, PathID = id, Type = type, Name = "Hero"
        };
        var builder = new AssetIndexStore.Builder();
        builder.Add(Row("CAB-hero", @"E:\a.blk", 10, 1, ClassIDType.GameObject));
        builder.Add(Row("CAB-hero", @"E:\a.blk", 10, 2, ClassIDType.Animator));
        builder.Add(Row("cab-HERO", @"E:\copy.blk", 20, 11, ClassIDType.GameObject));
        builder.Add(Row("CAB-other", @"E:\other.blk", 30, 1, ClassIDType.GameObject));
        var plan = builder.Build(GameType.ZZZ, "fixture").CollectDirectory("Assets/Hero", true, default);
        var members = plan.Groups.Single().Members;
        check(members.Length == 3 && plan.DuplicateRows == 1,
            "directory chooses a complete CAB copy even when duplicate PathIDs differ");
        check(members.Where(a => a.Cab.Equals("CAB-hero", StringComparison.OrdinalIgnoreCase))
                .All(a => a.Source == @"E:\a.blk" && a.Offset == 10),
            "primary-resource members never mix physical copies of one CAB");
        check(members.Any(a => a.Cab == "CAB-other" && a.PathID == 1),
            "identical names and PathIDs in different CABs remain distinct objects");

        var offsets = new AssetIndexStore.Builder();
        offsets.Add(Row("CAB-hero", @"E:\a.blk", 100, 1, ClassIDType.GameObject));
        offsets.Add(Row("CAB-hero", @"E:\a.blk", 200, 11, ClassIDType.GameObject));
        offsets.Add(Row("CAB-hero", @"E:\a.blk", 200, 12, ClassIDType.Animator));
        check(offsets.Build(GameType.ZZZ, "fixture").CollectDirectory("Assets/Hero", true, default)
                .Groups.Single().Members.All(a => a.Offset == 200),
            "copies in different bundle offsets of the same source stay separate");
    }

    private static ObjectReader Reader(Stream stream, GameType game, ClassIDType type, string hash)
    {
        var file = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
        file.version = [2019, 4, 40, 1];
        file.m_TargetPlatform = BuildTarget.StandaloneWindows64;
        file.header = new SerializedFileHeader { m_Version = SerializedFileFormatVersion.StoresTypeDependencies };
        return new ObjectReader(new EndianBinaryReader(stream, EndianType.LittleEndian), file,
            new ObjectInfo { byteStart = 8, byteSize = (uint)(stream.Length - 8), classID = (int)type,
                m_PathID = 1, serializedType = new SerializedType { m_OldTypeHash = Convert.FromHexString(hash) } },
            GameManager.GetGameByType(game));
    }

    private static MemoryStream ControllerFixture(bool typeID)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        void Align() { while (stream.Position % 4 != 0) writer.Write((byte)0); }
        void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); Align(); }
        writer.Write(0L);
        Text("FixtureController");
        writer.Write(0u); // Native controller memory size is not its serialized byte length.
        writer.Write(0); writer.Write(0); // Layers and state machines.
        writer.Write(2); // Parameter values.
        foreach (var (id, type) in new[] { (201u, 1u), (202u, 3u) })
        {
            writer.Write(id);
            if (typeID) writer.Write(0u);
            writer.Write(type); writer.Write(0u);
        }
        writer.Write(0); writer.Write(0); writer.Write(0); // Position, quaternion, scale defaults.
        writer.Write(1); writer.Write(1.25f); // Float defaults.
        writer.Write(1); writer.Write(7); // Integer defaults.
        writer.Write(1); writer.Write(true); Align(); // Boolean defaults.
        writer.Write(2); // String table.
        writer.Write(101u); Text("Base Layer.Idle");
        writer.Write(102u); Text("Base Layer.Walk");
        writer.Write(1); writer.Write(1); writer.Write(123456789L); // Animation reference.
        stream.Position = 0;
        return stream;
    }
}
