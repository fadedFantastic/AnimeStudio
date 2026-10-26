using System.Globalization;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace AnimeStudio.AssetExplorer;

// Research export: retain the serialized object and its source CAB byte for byte.
// No controller state-machine conversion or animation decoding is required.
public static class CompiledControllerExporter
{
    internal sealed record Located(CatalogAsset Asset, SerializedFile File, ObjectInfo Info);

    public static PrimaryExportResult Export(AssetsManager manager, PrimaryAssetGroup group, CatalogAsset[] selected,
        string output, CancellationToken token)
    {
        var main = Locate(manager, group, selected, token);
        var destination = Path.Combine(output, ResourcePaths.OutputSegment(Path.GetFileName(group.ResourcePath)));
        return Write(main.File, main.Info, main.Asset.Name, group.ResourcePath, destination, token);
    }

    internal static Located Locate(AssetsManager manager, PrimaryAssetGroup group, CatalogAsset[] selected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!group.IsCompiledController) throw new ArgumentException("请选择 .controller 资源。");
        var candidates = selected.Where(a => IsController(a.Type)).SelectMany(asset =>
            manager.assetsFileList.Where(file => SameFile(file, asset))
                .SelectMany(file => file.m_Objects.Where(info => info.m_PathID == asset.PathID && info.classID == (int)asset.Type)
                    .Select(info => new Located(asset, file, info)))).ToArray();
        Located main;
        if (candidates.Length == 1) main = candidates[0];
        else
        {
            // A preload table may reference more than one controller. Use the container's
            // primary pointer rather than selecting the first matching class.
            var primaries = new HashSet<Located>();
            foreach (var file in candidates.Select(c => c.File).Distinct())
                foreach (var info in file.m_Objects.Where(i => i.classID == (int)ClassIDType.AssetBundle))
                {
                    token.ThrowIfCancellationRequested();
                    var position = file.reader.Position;
                    try
                    {
                        var bundle = new AssetBundle(new ObjectReader(file.reader, file, info, manager.Game));
                        foreach (var entry in bundle.m_Container.Where(e => Matches(e.Key, group.ResourcePath)))
                        {
                            var pointer = entry.Value.asset;
                            var cab = pointer.m_FileID == 0 ? file.fileName
                                : pointer.m_FileID > 0 && pointer.m_FileID <= file.m_Externals.Count
                                    ? file.m_Externals[pointer.m_FileID - 1].fileName : null;
                            foreach (var candidate in candidates.Where(c => c.Info.m_PathID == pointer.m_PathID &&
                                c.File.fileName.Equals(cab, StringComparison.OrdinalIgnoreCase))) primaries.Add(candidate);
                        }
                    }
                    finally { file.reader.Position = position; }
                }
            if (primaries.Count != 1)
                throw new InvalidDataException($"无法唯一定位 {group.ResourcePath} 的主控制器（候选 {candidates.Length} 个）。");
            main = primaries.Single();
        }
        return main;
    }

    public static PrimaryExportResult Export(AnimeStudio.Object controller, string destination, string resourcePath,
        CancellationToken token)
    {
        if (!IsController(controller.type)) throw new ArgumentException("资源不是动画控制器。");
        var info = controller.assetsFile.m_Objects.Single(i => i.m_PathID == controller.m_PathID && i.classID == (int)controller.type);
        return Write(controller.assetsFile, info, controller.Name, resourcePath, destination, token);
    }

    private static bool IsController(ClassIDType type) => type is ClassIDType.AnimatorController or ClassIDType.AnimatorOverrideController;

    private static bool SameFile(SerializedFile file, CatalogAsset asset) =>
        Path.GetFullPath(file.originalPath ?? file.fullName).Equals(Path.GetFullPath(asset.Source), StringComparison.OrdinalIgnoreCase) &&
        (asset.Offset < 0 || file.offset == asset.Offset) &&
        (string.IsNullOrEmpty(asset.Cab) || file.fileName.Equals(asset.Cab, StringComparison.OrdinalIgnoreCase));

    private static bool Matches(string value, string resource) =>
        value.Replace('\\', '/').Equals(resource, StringComparison.OrdinalIgnoreCase) ||
        ulong.TryParse(value, out var hash) && hash == PathRecovery.HashOf(resource);

    private static PrimaryExportResult Write(SerializedFile file, ObjectInfo info, string name, string resource,
        string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        var cabPath = destination + ".cab";
        var metadataPath = destination + ".info.json";
        foreach (var path in new[] { destination, cabPath, metadataPath })
            if (File.Exists(path)) throw new IOException("目标文件已存在，未覆盖：" + path);
        var stream = file.reader.BaseStream;
        CheckRange(stream, 0, file.header.m_FileSize);
        CheckRange(stream, info.byteStart, info.byteSize);
        if (info.byteStart > file.header.m_FileSize || info.byteSize > file.header.m_FileSize - info.byteStart)
            throw new InvalidDataException("控制器数据超出 CAB 文件边界。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        var created = new List<string>();
        try
        {
            var mainHash = CopyRange(stream, info.byteStart, info.byteSize, destination, token, created);
            var cabHash = CopyRange(stream, 0, file.header.m_FileSize, cabPath, token, created);
            var metadata = new
            {
                FormatVersion = 1, Format = "UnitySerializedControllerObject", Purpose = "Original compiled data for research",
                ResourcePath = resource, Name = name, ClassID = info.classID, Type = ((ClassIDType)info.classID).ToString(),
                PathID = ID(info.m_PathID), Source = file.originalPath ?? file.fullName, BundleOffset = file.offset, Cab = file.fileName,
                Game = file.game?.Type.ToString(), UnityVersion = file.unityVersion,
                SerializedFileVersion = (int)file.header.m_Version, Endianness = file.reader.Endian.ToString(),
                MainDataFile = Path.GetFileName(destination), MainByteStartInCab = info.byteStart, MainByteSize = info.byteSize,
                MainSHA256 = mainHash, CabDataFile = Path.GetFileName(cabPath), CabByteSize = file.header.m_FileSize, CabSHA256 = cabHash,
                TypeHash = Hex(info.serializedType?.m_OldTypeHash), ScriptID = Hex(info.serializedType?.m_ScriptID),
                TypeTreeAvailable = info.serializedType?.m_Type?.m_Nodes?.Count > 0,
                ExternalDataIncluded = false,
                ExternalFiles = file.m_Externals.Select((external, i) => new
                { FileID = i + 1, Cab = external.fileName, Path = external.pathName, Guid = external.guid.ToString("N"), Type = external.type }),
                Objects = file.m_Objects.Select(obj => new
                {
                    PathID = ID(obj.m_PathID), ClassID = obj.classID, TypeIndex = obj.typeID,
                    ByteStartInCab = obj.byteStart, ByteSize = obj.byteSize, TypeHash = Hex(obj.serializedType?.m_OldTypeHash)
                })
            };
            token.ThrowIfCancellationRequested();
            using (var output = new FileStream(metadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created.Add(metadataPath);
                using var writer = new StreamWriter(output);
                writer.Write(JsonConvert.SerializeObject(metadata, Formatting.Indented));
            }
            token.ThrowIfCancellationRequested();
            return new([destination]);
        }
        catch
        {
            foreach (var path in created) File.Delete(path);
            throw;
        }
    }

    private static string CopyRange(Stream source, long start, long length, string destination, CancellationToken token,
        List<string> created)
    {
        CheckRange(source, start, length);
        var position = source.Position;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created.Add(destination);
            source.Position = start;
            var buffer = new byte[64 * 1024];
            while (length > 0)
            {
                token.ThrowIfCancellationRequested();
                var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
                if (count == 0) throw new EndOfStreamException("控制器或 CAB 原始数据不完整。");
                output.Write(buffer, 0, count); hash.AppendData(buffer, 0, count); length -= count;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { source.Position = position; }
    }

    private static void CheckRange(Stream stream, long start, long length)
    {
        if (start < 0 || length <= 0 || start > stream.Length || length > stream.Length - start)
            throw new InvalidDataException("控制器或 CAB 原始数据范围无效。");
    }

    private static string ID(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static string Hex(byte[] bytes) => bytes == null ? null : Convert.ToHexString(bytes);
}
