using Color = System.Drawing.Color;

namespace AnimeStudio.AssetExplorer;

internal sealed class AssetPreviewControl : UserControl
{
    private readonly IStudioBridge bridge;
    private readonly CheckBox auto = new() { Text = "自动预览图片 / 文本", Checked = true, AutoSize = true, Name = "autoPreview" };
    private readonly Label title = new() { Dock = DockStyle.Top, Height = 52, AutoEllipsis = true, Padding = new Padding(4), Name = "previewTitle" };
    private readonly Label info = new() { Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(4), Name = "previewInfo" };
    private readonly PictureBox picture = new CheckerPictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Visible = false, Name = "previewImage" };
    private readonly TextBox text = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Visible = false, Name = "previewText" };
    private readonly System.Windows.Forms.Timer debounce = new() { Interval = 300 };
    private readonly Dictionary<PreviewKey, AssetPreviewData> cache = [];
    private readonly LinkedList<PreviewKey> recent = new();
    private long cachedBytes, generation;
    private CatalogRequest request;
    private CancellationTokenSource pending;
    private bool available = true;
    private sealed record PreviewKey(GameType Game, string Root, string CabMap, CatalogAsset Asset);
    private static PreviewKey Key(CatalogRequest value) => value == null ? null : new(value.Game, value.SourceRoot, value.CabMap, value.Assets.Single());

    public AssetPreviewControl(IStudioBridge bridge)
    {
        this.bridge = bridge; Dock = DockStyle.Fill; Padding = new Padding(6); Name = "assetPreview";
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        header.Controls.Add(new Label { Text = "Preview", AutoSize = true, Padding = new Padding(0, 4, 6, 0) }); header.Controls.Add(auto);
        Controls.Add(picture); Controls.Add(text); Controls.Add(info); Controls.Add(title); Controls.Add(header);
        auto.CheckedChanged += (_, _) => Schedule();
        debounce.Tick += async (_, _) => { debounce.Stop(); await LoadAsync(); };
        Schedule();
    }

    public void SelectAsset(CatalogRequest value)
    {
        if (Equals(Key(request), Key(value))) return;
        request = value; Schedule();
    }

    public void Reset()
    {
        request = null; cache.Clear(); recent.Clear(); cachedBytes = 0; Schedule();
    }

    public void SetAvailable(bool value)
    {
        if (available == value) return;
        available = value; Schedule();
    }

    private void Schedule()
    {
        generation++; debounce.Stop(); pending?.Cancel(); ClearContent();
        var asset = request?.Assets.Single();
        title.Text = asset == null ? "选择资源以查看预览" : asset.Name + "\n" + asset.Container;
        if (asset == null) { info.Text = "支持 Texture2D、Sprite 图片及 TextAsset 文本。"; return; }
        info.Text = $"{asset.Type} · PathID {asset.PathID}\n{Path.GetFileName(asset.Source)} @ {asset.Offset}";
        if (!AssetPreview.CanLoad(asset.Type)) { info.Text += "\n此类型仅显示清单信息；模型可通过 Load Selected 在 Studio 中查看。"; return; }
        if (!auto.Checked) { info.Text += "\n自动预览已关闭。"; return; }
        if (!available) { info.Text += "\n任务进行中，暂缓预览以减少磁盘竞争。"; return; }
        info.Text += "\n等待预览…"; debounce.Start();
    }

    private async Task LoadAsync()
    {
        var snapshot = request; var version = generation;
        if (snapshot == null || !available || !auto.Checked || IsDisposed) return;
        var key = Key(snapshot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        pending = cancellation;
        Bitmap bitmap = null;
        try
        {
            info.Text = "正在读取选中资源…";
            var data = cache.GetValueOrDefault(key);
            var cached = data != null;
            data ??= await Task.Run(() => bridge.PreviewAsync(snapshot, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (data.ImagePng is { Length: > 0 })
                bitmap = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(data.ImagePng);
                    using var image = Image.FromStream(stream);
                    return new Bitmap(image);
                }, cancellation.Token);
            if (IsDisposed || cancellation.IsCancellationRequested || version != generation) return;
            if (!cached) Remember(key, data);
            else { recent.Remove(key); recent.AddLast(key); }
            info.Text = data.Info;
            text.Text = data.Text ?? ""; text.Visible = data.Text != null;
            picture.Image = bitmap; picture.Visible = bitmap != null; bitmap = null;
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && version == generation)
                info.Text = "预览超过 15 秒，已停止；可重新选择资源重试。";
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !cancellation.IsCancellationRequested && version == generation)
                info.Text = "无法预览：" + ex.GetBaseException().Message;
        }
        finally { bitmap?.Dispose(); if (ReferenceEquals(pending, cancellation)) pending = null; }
    }

    private void Remember(PreviewKey key, AssetPreviewData data)
    {
        cache[key] = data; recent.AddLast(key); cachedBytes += Size(data);
        while (recent.Count > 12 || cachedBytes > 16 * 1024 * 1024)
        {
            var oldest = recent.First.Value; recent.RemoveFirst(); cachedBytes -= Size(cache[oldest]); cache.Remove(oldest);
        }
    }

    private static long Size(AssetPreviewData data) => (data.ImagePng?.LongLength ?? 0) + (data.Text?.Length ?? 0) * 2L;
    private void ClearContent()
    {
        var old = picture.Image; picture.Image = null; picture.Visible = text.Visible = false; text.Clear(); old?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { generation++; debounce.Stop(); pending?.Cancel(); debounce.Dispose(); ClearContent(); cache.Clear(); }
        base.Dispose(disposing);
    }

    private sealed class CheckerPictureBox : PictureBox
    {
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(System.Drawing.Color.White);
            for (var y = 0; y < Height; y += 12)
                for (var x = 0; x < Width; x += 12)
                    if ((x / 12 + y / 12) % 2 == 0) e.Graphics.FillRectangle(Brushes.LightGray, x, y, 12, 12);
        }
    }
}
