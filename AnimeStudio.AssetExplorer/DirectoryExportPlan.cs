using System.Security.Cryptography;
using System.Text;

namespace AnimeStudio.AssetExplorer;

public sealed record PrimaryAssetGroup(string ResourcePath, CatalogAsset[] Members)
{
    public bool IsFbx => ResourcePath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
}

public sealed record DirectoryExportPlan(string Directory, bool Recursive, PrimaryAssetGroup[] Groups, int MatchedRows, int DuplicateRows);

public static class ResourcePaths
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请输入 Assets/... 资源目录。");
        var parts = path.Trim().Trim('"').Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("Assets", StringComparison.OrdinalIgnoreCase) || parts.Any(p => p is "." or ".."))
            throw new ArgumentException("请使用清单中的 Assets/... 资源路径，不是磁盘目录或推测路径。");
        parts[0] = "Assets";
        return string.Join('/', parts);
    }

    public static bool IsWithin(string resource, string directory, bool recursive)
    {
        var prefix = directory + "/";
        return resource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            (recursive || resource.IndexOf('/', prefix.Length) < 0);
    }

    public static string Parent(string resource)
    {
        try { var path = Normalize(resource); var slash = path.LastIndexOf('/'); return slash > 0 ? path[..slash] : "Assets"; }
        catch (ArgumentException) { return ""; }
    }

    public static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        if (name.Length == 0) name = "asset";
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])) name = "_" + name;
        return name[..Math.Min(name.Length, 100)];
    }

    public static string OutputPath(string outputRoot, string sourceDirectory, string resourcePath)
    {
        var resource = Normalize(resourcePath); var directory = Normalize(sourceDirectory);
        if (!IsWithin(resource, directory, true)) throw new ArgumentException("资源不属于所选目录。");
        var relative = resource[(directory.Length + 1)..].Split('/').Select(OutputSegment).ToArray();
        return ContainedPath(outputRoot, Path.Combine(relative));
    }

    public static string OutputDirectory(string outputRoot, string sourceDirectory, string resourcePath)
        => Path.GetDirectoryName(OutputPath(outputRoot, sourceDirectory, resourcePath));

    public static string OutputSegment(string value)
    {
        var stem = value.Split('.')[0].ToUpperInvariant();
        var reserved = new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]);
        if (value.Length is > 0 and <= 255 && !reserved && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && value.TrimEnd(' ', '.') == value)
            return value;
        var safe = SafeSegment(value);
        if (safe == value) return value;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
        return Path.GetFileNameWithoutExtension(safe) + "-" + hash + Path.GetExtension(safe);
    }

    public static string ContainedPath(string root, string relative)
    {
        root = Path.GetFullPath(root);
        var destination = Path.GetFullPath(Path.Combine(root, relative));
        if (!destination.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("输出路径越界。");
        return destination;
    }
}
