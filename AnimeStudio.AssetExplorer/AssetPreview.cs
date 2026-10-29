using System.Text;

namespace AnimeStudio.AssetExplorer;

public sealed record AssetPreviewData(string Info, string Text = null, byte[] ImagePng = null);

public static class AssetPreview
{
    public const int TextByteLimit = 64 * 1024;
    public const int ThumbnailSize = 768;
    public const long PixelLimit = 16 * 1024 * 1024;

    public static bool CanLoad(ClassIDType type) => type is ClassIDType.Texture2D or ClassIDType.Sprite or ClassIDType.TextAsset;

    public static CatalogAsset Representative(PrimaryAssetGroup group)
    {
        if (group.IsFbx || group.IsCompiledController || group.Extension is ".prefab" or ".mat" or ".spriteatlas")
            return group.Members.FirstOrDefault(a => !CanLoad(a.Type)) ?? group.Members.FirstOrDefault();
        return group.Members.FirstOrDefault(a => a.Type == ClassIDType.Sprite)
            ?? group.Members.FirstOrDefault(a => a.Type == ClassIDType.Texture2D)
            ?? group.Members.FirstOrDefault(a => a.Type == ClassIDType.TextAsset) ?? group.Members.FirstOrDefault();
    }

    public static AssetPreviewData DecodeText(byte[] bytes, int totalBytes)
    {
        var length = Math.Min(bytes.Length, TextByteLimit);
        Encoding encoding = new UTF8Encoding(false, true);
        var start = 0;
        if (length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { encoding = new UnicodeEncoding(false, true, true); start = 2; }
        else if (length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) { encoding = new UnicodeEncoding(true, true, true); start = 2; }
        else if (length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) start = 3;
        try
        {
            var chars = new char[encoding.GetMaxCharCount(length - start)];
            encoding.GetDecoder().Convert(bytes, start, length - start, chars, 0, chars.Length,
                totalBytes <= length, out _, out var used, out _);
            var text = new string(chars, 0, used);
            if (text.Any(c => c == '\0') || text.Count(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) > Math.Max(1, text.Length / 100))
                return new($"二进制 TextAsset · {totalBytes:N0} 字节，不自动转储内容。");
            return new($"文本 · {totalBytes:N0} 字节" + (totalBytes > length ? " · 仅显示前 64 KiB" : ""), text);
        }
        catch (DecoderFallbackException) { return new($"二进制或未知编码 TextAsset · {totalBytes:N0} 字节，不自动转储内容。"); }
    }
}
