using SkiaSharp;

namespace AnimeStudio.AssetExplorer;

// This reader belongs to the preview subprocess. It never builds Studio's asset tree or parses a CAB's other objects.
public sealed class AssetPreviewReader(string unityVersion = null) : IDisposable
{
    private AssetsManager manager;
    private (GameType Game, string Source, long Offset) location;
    private CabCatalog map;
    private (string Path, long Length, DateTime Modified) mapKey;

    public AssetPreviewData Read(CatalogRequest request, CancellationToken token)
    {
        var asset = request.Assets.Single();
        if (!AssetPreview.CanLoad(asset.Type)) return new("此类型仅显示清单信息。");
        if (asset.Offset < 0 && Path.GetExtension(asset.Source).ToLowerInvariant() is ".blk" or ".block")
            return new("清单缺少 bundle 偏移，已跳过整块加载；请更新索引后预览。");
        var plan = Plan(request, token);
        var selected = plan.Selected.Single();
        var next = (request.Game, selected.Source, selected.Offset);
        if (manager == null || location != next)
        {
            Dispose(); location = next;
            manager = new AssetsManager { Game = GameManager.GetGameByType(request.Game), SpecifyUnityVersion = unityVersion,
                SkipProcess = true, ResolveDependencies = false, SearchResourceFilesRecursively = false };
            Load(plan, token);
        }
        try
        {
            var file = manager.assetsFileList.SingleOrDefault(f =>
                Path.GetFullPath(f.originalPath ?? f.fullName).Equals(selected.Source, StringComparison.OrdinalIgnoreCase) &&
                (selected.Offset < 0 || f.offset == selected.Offset) &&
                (string.IsNullOrEmpty(selected.Cab) || f.fileName.Equals(selected.Cab, StringComparison.OrdinalIgnoreCase)));
            if (file == null) throw new InvalidDataException("无法定位资源所在 CAB，请更新清单。");
            var info = file.m_Objects.SingleOrDefault(o => o.m_PathID == selected.PathID && o.classID == (int)selected.Type)
                ?? throw new InvalidDataException("没有找到选中的对象，请更新清单。");
            var reader = new ObjectReader(file.reader, file, info, manager.Game);
            token.ThrowIfCancellationRequested();
            if (selected.Type == ClassIDType.TextAsset)
            {
                _ = new TextHeader(reader);
                var size = reader.ReadInt32();
                if (size < 0 || size > reader.BytesLeft()) throw new InvalidDataException("文本长度超出对象边界。");
                return AssetPreview.DecodeText(reader.ReadBytes(Math.Min(size, AssetPreview.TextByteLimit)), size);
            }
            if (info.byteSize > 8 * 1024 * 1024 && selected.Type == ClassIDType.Sprite)
                return new("Sprite 对象过大，已跳过自动预览；可导出图片查看。");
            if (selected.Type == ClassIDType.Texture2D)
            {
                var texture = new Texture2D(reader);
                var reason = CheckTexture(texture);
                if (reason != null) return new(reason);
                using var image = texture.ConvertToImage(true);
                return Thumbnail(image, $"{texture.m_Width} × {texture.m_Height} · {texture.m_TextureFormat}");
            }
            var sprite = new Sprite(reader);
            var atlas = LoadPointer(file, sprite.m_SpriteAtlas, request, token);
            Texture2D source;
            float downscale;
            if (atlas != null && atlas.m_RenderDataMap.TryGetValue(sprite.m_RenderDataKey, out var atlasData))
            {
                source = LoadPointer(atlas.assetsFile, atlasData.texture, request, token); downscale = atlasData.downscaleMultiplier;
            }
            else { source = LoadPointer(file, sprite.m_RD.texture, request, token); downscale = sprite.m_RD.downscaleMultiplier; }
            if (source == null) return new("Sprite 没有可读取的贴图；请检查 CABMap 和资源根目录。");
            var error = CheckTexture(source);
            if (error != null) return new(error);
            if (downscale > 0 && downscale < 1 && (double)source.m_Width * source.m_Height / downscale / downscale > AssetPreview.PixelLimit)
                return new("Sprite 还原尺寸过大，已跳过自动预览；可导出图片查看。");
            using var spriteImage = sprite.GetImage();
            return Thumbnail(spriteImage, $"Sprite · {spriteImage?.Width} × {spriteImage?.Height}");
        }
        finally
        {
            // Retain only small CABs for adjacent selections, never a large decompressed block indefinitely.
            if (manager != null && manager.assetsFileList.Sum(f => f.reader.BaseStream.Length) > 128 * 1024 * 1024L) Dispose();
        }
    }

    private LoadPlan Plan(CatalogRequest request, CancellationToken token)
    {
        var source = Path.GetFullPath(request.Assets.Single().Source);
        var root = string.IsNullOrWhiteSpace(request.SourceRoot) ? null : Path.GetFullPath(request.SourceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // An already-correct physical path needs no CABMap read, even for a multi-million-row map.
        if (File.Exists(source) && (root == null || source.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            request = request with { CabMap = "", SourceRoot = "" };
        var plan = CabCatalog.Plan(request, token, Map(request.CabMap, token), includeDependencies: false);
        if (plan.Missing.Length > 0) throw new FileNotFoundException(string.Join(Environment.NewLine, plan.Missing));
        return plan;
    }

    private CabCatalog Map(string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var info = new FileInfo(path); var key = (info.FullName, info.Length, info.LastWriteTimeUtc);
        if (map == null || mapKey != key) { map = CabCatalog.Read(path, token); mapKey = key; }
        return map;
    }

    private void Load(LoadPlan plan, CancellationToken token)
    {
        manager.FilterData.Items = plan.Offsets.ToList();
        using (token.Register(() => manager.tokenSource.Cancel())) manager.LoadFiles(plan.Files, mergeSplitAssets: false);
        token.ThrowIfCancellationRequested();
    }

    private T LoadPointer<T>(SerializedFile owner, PPtr<T> pointer, CatalogRequest request, CancellationToken token) where T : AnimeStudio.Object
    {
        if (pointer == null || pointer.IsNull) return null;
        var file = owner;
        if (pointer.m_FileID != 0)
        {
            if (pointer.m_FileID < 1 || pointer.m_FileID > owner.m_Externals.Count) throw new InvalidDataException("Sprite 的外部引用无效。");
            var cab = owner.m_Externals[pointer.m_FileID - 1].fileName;
            file = manager.assetsFileList.FirstOrDefault(f => f.fileName.Equals(cab, StringComparison.OrdinalIgnoreCase));
            if (file == null)
            {
                var entries = Map(request.CabMap, token)?.FindCab(cab) ?? [];
                var entry = entries.FirstOrDefault(e => e.Source.Equals(owner.originalPath ?? owner.fullName, StringComparison.OrdinalIgnoreCase)) ?? entries.FirstOrDefault();
                if (entry == null) throw new FileNotFoundException("Sprite 图片需要直接引用的 " + cab + "，请指定 CABMap。");
                var row = new CatalogAsset { Source = entry.Source, Offset = entry.Offset, Cab = cab };
                Load(Plan(request with { Assets = [row] }, token), token);
                file = manager.assetsFileList.FirstOrDefault(f => f.fileName.Equals(cab, StringComparison.OrdinalIgnoreCase));
            }
        }
        if (file == null) return null;
        if (file.ObjectsDic.TryGetValue(pointer.m_PathID, out var existing)) return existing as T;
        var info = file.m_Objects.SingleOrDefault(o => o.m_PathID == pointer.m_PathID);
        if (info == null) return null;
        if (info.byteSize > 8 * 1024 * 1024 && info.classID != (int)ClassIDType.Texture2D)
            throw new InvalidDataException("Sprite 依赖对象过大，已停止自动预览。");
        var reader = new ObjectReader(file.reader, file, info, manager.Game);
        AnimeStudio.Object result = (ClassIDType)info.classID switch
        {
            ClassIDType.Texture2D => new Texture2D(reader), ClassIDType.SpriteAtlas => new SpriteAtlas(reader),
            _ => throw new InvalidDataException("Sprite 引用的对象不是贴图或图集。")
        };
        file.AddObject(result); return result as T;
    }

    private static string CheckTexture(Texture2D texture)
    {
        if (texture.m_Width <= 0 || texture.m_Height <= 0 || (long)texture.m_Width * texture.m_Height > AssetPreview.PixelLimit ||
            texture.image_data.Size < 0 || texture.image_data.Size > 64 * 1024 * 1024)
            return $"图片 {texture.m_Width} × {texture.m_Height} 超出自动预览大小限制；可导出图片查看。";
        return null;
    }

    private static AssetPreviewData Thumbnail(SKBitmap image, string info)
    {
        if (image == null) return new("此图片格式暂不能解码预览。");
        using var stream = new MemoryStream();
        var scale = Math.Min(1, (double)AssetPreview.ThumbnailSize / Math.Max(image.Width, image.Height));
        if (scale < 1)
        {
            using var resized = image.Resize(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
            resized.WriteToStream(stream, ImageFormat.Png);
        }
        else image.WriteToStream(stream, ImageFormat.Png);
        return new(info, ImagePng: stream.ToArray());
    }

    private sealed class TextHeader(ObjectReader reader) : NamedObject(reader);

    public void Dispose() { manager?.Clear(); manager = null; }
}
