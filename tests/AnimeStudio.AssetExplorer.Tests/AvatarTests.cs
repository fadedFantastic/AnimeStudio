using System.Runtime.CompilerServices;
using System.Text;
using AnimeStudio;

internal static class AvatarTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var (game, flag) in new[] { (GameType.ZZZ, (bool?)null), (GameType.ZZZ, false), (GameType.ZZZ, true), (GameType.Normal, null) })
        {
            using var stream = Fixture(flag);
            using var reader = Reader(stream, game, flag);
            var avatar = new Avatar(reader);
            check(avatar.FindBonePath(859800726) == "Bip001/Bip001 Pelvis" && avatar.FindBonePath(1110389040) == "Bip001",
                $"Avatar path table stays aligned: game={game}, root-motion flag={flag?.ToString() ?? "absent"}");
            check(avatar.m_Avatar.m_UseNextLevelForRootMotionSkeleton == (flag ?? false),
                "Avatar preserves the root-motion flag when present");
            check(avatar.m_HumanDescription.m_RootMotionBoneName == "Bip001" && reader.BytesLeft() == 0,
                "Avatar reads the complete HumanDescription without crossing the object boundary");
        }
    }

    private static ObjectReader Reader(Stream stream, GameType game, bool? flag)
    {
        var file = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
        file.version = [2019, 4, 40, 1];
        file.m_TargetPlatform = BuildTarget.StandaloneWindows64;
        file.header = new SerializedFileHeader();
        var type = new SerializedType { m_OldTypeHash = flag.HasValue || game != GameType.ZZZ
            ? new byte[16] : Convert.FromHexString("06FC117CA6D965140CCC18CA840CC1A1") };
        return new ObjectReader(new EndianBinaryReader(stream, EndianType.LittleEndian), file,
            new ObjectInfo { byteStart = 8, byteSize = (uint)(stream.Length - 8), classID = (int)ClassIDType.Avatar,
                m_PathID = 1, serializedType = type }, GameManager.GetGameByType(game));
    }

    private static MemoryStream Fixture(bool? flag)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        void Align() { while (stream.Position % 4 != 0) writer.Write((byte)0); }
        void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); Align(); }
        void Skeleton() { writer.Write(0); writer.Write(0); writer.Write(0); }
        void XForm() { for (var i = 0; i < 10; i++) writer.Write(i >= 6 ? 1f : 0f); }
        writer.Write(0L); // Nonzero object offset.
        Text("FixtureAvatar");
        var sizePosition = stream.Position;
        writer.Write(0u);
        var constantStart = stream.Position;
        Skeleton(); // Avatar skeleton.
        writer.Write(0); // Skeleton pose.
        writer.Write(0); // Default pose.
        writer.Write(0); // Skeleton name IDs.
        XForm(); Skeleton(); // Human root and skeleton.
        writer.Write(0); // Human skeleton pose.
        writer.Write(0); writer.Write(0); // Hands.
        writer.Write(0); writer.Write(0); // Human bone indices and masses.
        for (var i = 0; i < 8; i++) writer.Write(0f);
        writer.Write(false); writer.Write(false); writer.Write(false); Align();
        writer.Write(0); writer.Write(0); // Human skeleton indices and reverse indices.
        writer.Write(-1); XForm(); // Root motion bone.
        Skeleton(); writer.Write(0); writer.Write(0); // Root motion skeleton, pose, indices.
        if (flag.HasValue) { writer.Write(flag.Value); Align(); }
        var constantEnd = stream.Position;
        stream.Position = sizePosition;
        writer.Write((uint)(constantEnd - constantStart));
        stream.Position = constantEnd;
        writer.Write(2); // m_TOS.
        writer.Write(859800726u); Text("Bip001/Bip001 Pelvis");
        writer.Write(1110389040u); Text("Bip001");
        writer.Write(0); writer.Write(0); // HumanDescription human/skeleton arrays.
        for (var i = 0; i < 8; i++) writer.Write(0.5f);
        Text("Bip001");
        writer.Write(false); writer.Write(true); writer.Write(true); Align();
        stream.Position = 0;
        return stream;
    }
}
