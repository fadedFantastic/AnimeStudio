internal static class AclProbe
{
    // ACL 缓冲区布局: raw_buffer_header { u32 size; u32 hash } + tracks_header { u32 tag; u16 version; u8 algorithm; u8 trackType; u32 tracks; u32 samples; f32 rate; u32 misc }
    public static void DumpBuffer(List<string> dump, string label, byte[] data, int offset)
    {
        if (data == null || offset < 0 || offset + 32 > data.Length)
        {
            dump.Add($"{label}: unavailable (len={(data?.Length ?? 0)}, offset={offset})");
            return;
        }
        var size = BitConverter.ToUInt32(data, offset);
        var hash = BitConverter.ToUInt32(data, offset + 4);
        var tag = BitConverter.ToUInt32(data, offset + 8);
        var version = BitConverter.ToUInt16(data, offset + 12);
        var algorithm = data[offset + 14];
        var trackType = data[offset + 15];
        var numTracks = BitConverter.ToUInt32(data, offset + 16);
        var numSamples = BitConverter.ToUInt32(data, offset + 20);
        var sampleRate = BitConverter.ToSingle(data, offset + 24);
        var misc = BitConverter.ToUInt32(data, offset + 28);
        dump.Add($"{label}: size={size} hash=0x{hash:X8} tag=0x{tag:X8} version={version}({VersionName(version)}) algorithm={algorithm} trackType={trackType}({TrackTypeName(trackType)}) tracks={numTracks} samples={numSamples} rate={sampleRate} misc=0x{misc:X8}");
    }

    private static string VersionName(int version) => version switch
    {
        7 => "v02_00_00", 8 => "v02_01_99", 9 => "v02_01_99_1", 10 => "v02_01_00", 100 => "vHoYo", _ => "unknown",
    };

    private static string TrackTypeName(int trackType) => trackType switch
    {
        0 => "float1f", 1 => "float2f", 2 => "float3f", 3 => "float4f", 4 => "vector4f", 12 => "qvvf", _ => "unknown",
    };
}
