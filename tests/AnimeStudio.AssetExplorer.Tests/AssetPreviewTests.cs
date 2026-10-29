using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using AnimeStudio;
using AnimeStudio.AssetExplorer;
using SkiaSharp;

internal static class AssetPreviewTests
{
    public static void RunReal(string output, string catalogPath, string cabMap, string sourceRoot, string guiPath, Action<bool, string> check)
    {
        var catalog = AssetIndexStore.Load(catalogPath, output, Console.WriteLine, default);
        var gui = Assembly.LoadFrom(Path.GetFullPath(guiPath));
        var type = gui.GetType("AnimeStudio.GUI.AssetPreviewWorker+Client", true);
        using var client = (IDisposable)Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { "" }, null);
        var read = type.GetMethod("ReadAsync").CreateDelegate<Func<CatalogRequest, CancellationToken, Task<AssetPreviewData>>>(client);
        var results = new List<object>();
        foreach (var assetType in new[] { ClassIDType.Texture2D, ClassIDType.Sprite, ClassIDType.TextAsset })
        {
            var loaded = 0;
            foreach (var index in catalog.Search(new("", assetType.ToString(), "", false), default).Take(12))
            {
                var asset = catalog.Entry(index); var timer = System.Diagnostics.Stopwatch.StartNew();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    var result = read(new(catalog.Game, [asset], cabMap, sourceRoot), timeout.Token).GetAwaiter().GetResult();
                    Console.WriteLine($"REAL {asset.Type} {asset.Name}: {timer.ElapsedMilliseconds}ms · {result.Info}");
                    results.Add(new { asset, elapsedMs = timer.ElapsedMilliseconds, result.Info });
                    if (result.ImagePng?.Length > 0)
                    {
                        using var bitmap = SKBitmap.Decode(result.ImagePng);
                        check(bitmap != null && bitmap.Width <= AssetPreview.ThumbnailSize && bitmap.Height <= AssetPreview.ThumbnailSize,
                            "real game image has a bounded decoded thumbnail: " + asset.Name);
                        File.WriteAllBytes(Path.Combine(output, assetType + "-" + loaded + ".png"), result.ImagePng); loaded++;
                    }
                    else if (assetType == ClassIDType.TextAsset && result.Text != null)
                    {
                        check(result.Text.Length <= AssetPreview.TextByteLimit, "real game text has a bounded preview: " + asset.Name); loaded++;
                    }
                    if (loaded >= (assetType == ClassIDType.Texture2D ? 3 : 1)) break;
                }
                catch (Exception ex) { Console.WriteLine("REAL " + asset.Name + ": " + ex.GetBaseException().Message); results.Add(new { asset, error = ex.GetBaseException().Message }); }
            }
            check(loaded > 0, "real game supports " + assetType + " preview");
        }
        File.WriteAllText(Path.Combine(output, "real-preview.json"), Newtonsoft.Json.JsonConvert.SerializeObject(results, Newtonsoft.Json.Formatting.Indented));
    }

    public static void Run(string output, string guiPath, Action<bool, string> check)
    {
        check(AssetPreview.DecodeText(Encoding.UTF8.GetBytes("你好\nconfig=true"), 18).Text?.Contains("你好") == true, "UTF-8 text remains readable");
        var unicode = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("文本预览")).ToArray();
        check(AssetPreview.DecodeText(unicode, unicode.Length).Text == "文本预览", "UTF-16 text with BOM remains readable");
        check(AssetPreview.DecodeText([0, 1, 2, 3], 4).Text == null, "binary TextAssets are not dumped into the UI");
        var partial = Encoding.UTF8.GetBytes(new string('x', AssetPreview.TextByteLimit - 1) + "中");
        check(AssetPreview.DecodeText(partial[..AssetPreview.TextByteLimit], partial.Length).Text?.Length == AssetPreview.TextByteLimit - 1,
            "truncation never turns a partial UTF-8 character into a binary detection");
        var fixture = WriteFixture(output);
        using (var reader = new AssetPreviewReader())
        {
            // A correct physical source must not open the deliberately nonexistent dependency map.
            var request = new CatalogRequest(GameType.Normal, [fixture.Texture], Path.Combine(output, "missing.bin"), output);
            var image = reader.Read(request, default);
            using var bitmap = SKBitmap.Decode(image.ImagePng);
            check(bitmap is { Width: 192, Height: 128 } && bitmap.GetPixel(0, 0) != bitmap.GetPixel(180, 100),
                "preview decodes real Texture2D bytes while ignoring other objects and dependency maps");
            File.WriteAllBytes(Path.Combine(output, "texture.png"), image.ImagePng);
            var sprite = reader.Read(request with { Assets = [fixture.Sprite] }, default);
            using var spriteBitmap = SKBitmap.Decode(sprite.ImagePng);
            check(spriteBitmap is { Width: 32, Height: 16 }, "Sprite preview reads only its local texture and crops the image");
            var text = reader.Read(request with { Assets = [fixture.Text] }, default);
            check(text.Text?.Length == AssetPreview.TextByteLimit && text.Info.Contains("64 KiB"), "large serialized TextAsset reads only a bounded prefix");
            var large = reader.Read(request with { Assets = [fixture.Large] }, default);
            check(large.ImagePng == null && large.Info.Contains("大小限制"), "oversized texture stops before allocating decoded pixels");
            check(reader.Read(request with { Assets = [fixture.Texture with { Type = ClassIDType.Mesh }] }, default).ImagePng == null,
                "model selection never triggers image or mesh loading");
            check(reader.Read(request with { Assets = [fixture.Texture with { Source = "legacy.blk", Offset = -1 }] }, default).Info.Contains("bundle 偏移"),
                "legacy block without an offset is never loaded in full for preview");
        }
        if (!string.IsNullOrEmpty(guiPath)) RunWorker(guiPath, fixture.Texture, check);
        ExplorerUiTests.RunPreview(output, fixture.Texture, fixture.Text, check);
    }

    private static void RunWorker(string guiPath, CatalogAsset texture, Action<bool, string> check)
    {
        var gui = Assembly.LoadFrom(Path.GetFullPath(guiPath));
        var type = gui.GetType("AnimeStudio.GUI.AssetPreviewWorker+Client", true);
        using var client = (IDisposable)Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { "" }, null);
        var read = type.GetMethod("ReadAsync").CreateDelegate<Func<CatalogRequest, CancellationToken, Task<AssetPreviewData>>>(client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = new CatalogRequest(GameType.Normal, [texture], "", "");
        using (var interrupted = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            try { read(request, interrupted.Token).GetAwaiter().GetResult(); throw new Exception("Startup cancellation ignored"); }
            catch (OperationCanceledException) { check(true, "in-flight worker startup can be cancelled and stopped"); }
        }
        var result = read(request, timeout.Token).GetAwaiter().GetResult();
        check(result.ImagePng?.Length > 0, "GUI subprocess protocol returns a decoded image");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { read(request, cancelled.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { check(true, "preview client rejects cancelled work before loading"); }
        check(read(request, timeout.Token).GetAwaiter().GetResult().ImagePng?.Length > 0, "preview worker remains reusable after a cancelled queued request");
    }

    private sealed record Fixture(CatalogAsset Texture, CatalogAsset Sprite, CatalogAsset Text, CatalogAsset Large);

    private static Fixture WriteFixture(string output)
    {
        static byte[] Bytes(Action<BinaryWriter> write) { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); write(writer); return stream.ToArray(); }
        static void Align(BinaryWriter writer) { while (writer.BaseStream.Position % 4 != 0) writer.Write((byte)0); }
        static void Name(BinaryWriter writer, string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); Align(writer); }
        static void Floats(BinaryWriter writer, params float[] values) { foreach (var value in values) writer.Write(value); }
        static void Pointer(BinaryWriter writer, long id) { writer.Write(0); writer.Write(id); }
        byte[] Texture(string name, int width, int height, byte[] pixels) => Bytes(w =>
        {
            Name(w, name); w.Write(0); w.Write(false); Align(w);
            w.Write(width); w.Write(height); w.Write(pixels.Length); w.Write((int)TextureFormat.RGBA32); w.Write(1);
            w.Write(true); w.Write(false); Align(w); w.Write(0); w.Write(1); w.Write(2);
            w.Write(0); w.Write(1); w.Write(0f); w.Write(1); w.Write(1); w.Write(1); w.Write(0); w.Write(1);
            w.Write(pixels.Length); w.Write(pixels);
        });
        var pixels = new byte[192 * 128 * 4];
        for (var y = 0; y < 128; y++) for (var x = 0; x < 192; x++)
        { var p = (y * 192 + x) * 4; pixels[p] = (byte)x; pixels[p + 1] = (byte)(y * 2); pixels[p + 2] = 100; pixels[p + 3] = (byte)(x < 30 ? 100 : 255); }
        var sprite = Bytes(w =>
        {
            Name(w, "Sprite"); Floats(w, 0, 0, 32, 16, 0, 0, 0, 0, 0, 0, 100, .5f, .5f);
            w.Write(0u); w.Write(false); Align(w); w.Write(new byte[16]); w.Write(0L); w.Write(0); Pointer(w, 0);
            Pointer(w, 1); Pointer(w, 0); w.Write(0); w.Write(0); // Submeshes, indices.
            w.Write(0u); w.Write(1); w.Write(new byte[4]); w.Write(0); // Empty vertex data with one channel.
            w.Write(0); Floats(w, 10, 20, 32, 16, 0, 0, 0, 0); w.Write(2u); Floats(w, 0, 0, 0, 0, 1); w.Write(0);
        });
        (ClassIDType Type, byte[] Data)[] objects = [ (ClassIDType.Texture2D, Texture("Texture", 192, 128, pixels)),
            (ClassIDType.Sprite, sprite), (ClassIDType.TextAsset, Bytes(w => { Name(w, "Text"); w.Write(200000); w.Write(Encoding.UTF8.GetBytes(new string('a', 200000))); })),
            (ClassIDType.Texture2D, Texture("Large", 8192, 8192, new byte[4])), (ClassIDType.Mesh, new byte[4]) ];
        var path = Path.Combine(output, "preview-fixture.assets");
        using var file = new MemoryStream(); using var writer = new BinaryWriter(file);
        writer.Write(new byte[20]); writer.Write(Encoding.UTF8.GetBytes("2018.4.0f1\0")); writer.Write((int)BuildTarget.StandaloneWindows64); writer.Write(false);
        var types = objects.Select(o => o.Type).Distinct().ToArray(); writer.Write(types.Length);
        foreach (var type in types) { writer.Write((int)type); writer.Write(false); writer.Write((short)-1); writer.Write(new byte[16]); }
        writer.Write(objects.Length); var start = 0;
        for (var i = 0; i < objects.Length; i++)
        { Align(writer); writer.Write((long)i + 1); writer.Write((uint)start); writer.Write((uint)objects[i].Data.Length); writer.Write(Array.IndexOf(types, objects[i].Type)); start += objects[i].Data.Length; }
        writer.Write(0); writer.Write(0); writer.Write((byte)0); var metadata = file.Position - 20;
        while (file.Position % 16 != 0) writer.Write((byte)0); var offset = (uint)file.Position;
        foreach (var obj in objects) writer.Write(obj.Data);
        var data = file.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(data, (uint)metadata); BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 17); BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), offset); File.WriteAllBytes(path, data);
        CatalogAsset Row(int index, string name, string container) => new() { Name = name, Type = objects[index].Type, Source = path, PathID = index + 1, Container = container };
        return new(Row(0, "Texture", "Assets/Preview/Texture.png"), Row(1, "Sprite", "Assets/Preview/Sprite.png"),
            Row(2, "Text", "Assets/Preview/Text.json"), Row(3, "Large", "Assets/Preview/Large.png"));
    }
}
