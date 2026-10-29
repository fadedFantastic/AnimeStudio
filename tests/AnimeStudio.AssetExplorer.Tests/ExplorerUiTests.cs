using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using AnimeStudio;
using AnimeStudio.AssetExplorer;

internal static class ExplorerUiTests
{
    public static void RunPreview(string output, CatalogAsset texture, CatalogAsset textAsset, Action<bool, string> check)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            using var timer = new System.Windows.Forms.Timer { Interval = 1 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    var builder = new AssetIndexStore.Builder(); builder.Add(texture); builder.Add(textAsset);
                    builder.Add(texture with { Name = "Slow", PathID = 10, Container = "Assets/Preview/Slow.png" });
                    builder.Add(texture with { Name = "Model", PathID = 11, Type = ClassIDType.Mesh, Container = "Assets/Preview/Model.mesh" });
                    var path = Path.Combine(output, "preview-ui.aex"); builder.Build(GameType.Normal, "fixture").Save(path, default);
                    var bridge = new PreviewTestBridge(File.ReadAllBytes(Path.Combine(output, "texture.png")));
                    using var form = new ExplorerForm(bridge, false, new ExplorerSettings { WorkDirectory = output }, _ => { }, ex => throw ex) { Opacity = 0, ShowInTaskbar = false };
                    form.Show(); Complete(form.OpenAsync(path));
                    var list = (ListView)form.Controls.Find("searchResults", true).Single();
                    var pages = (TabControl)form.Controls.Find("explorerPages", true).Single();
                    var picture = (PictureBox)form.Controls.Find("previewImage", true).Single();
                    var text = (TextBox)form.Controls.Find("previewText", true).Single();
                    var info = (Label)form.Controls.Find("previewInfo", true).Single();
                    var auto = (CheckBox)form.Controls.Find("autoPreview", true).Single();
                    var directory = Descendants(form).OfType<DirectoryExportControl>().Single();
                    void Select(int index) { list.SelectedIndices.Clear(); list.Items[index].Selected = true; list.Items[index].Focused = true; }
                    Select(0); PumpUntil(() => picture.Image != null);
                    check(picture.Visible && bridge.Calls == 1, "search selection displays an image without Load Selected");
                    list.SelectedIndices.Clear(); Select(0); PumpUntil(() => picture.Image != null);
                    check(bridge.Calls == 1, "repeat image selection uses bounded thumbnail cache");
                    pages.SelectedIndex = 1; directory.SelectDirectory("Assets/Preview"); Complete(directory.CollectAsync());
                    var rows = (ListView)form.Controls.Find("resourcePreview", true).Single();
                    var imageIndex = Enumerable.Range(0, rows.VirtualListSize).Single(i => rows.Items[i].Text == "Texture.png");
                    rows.Items[imageIndex].Selected = true; PumpUntil(() => picture.Image != null);
                    check(bridge.Calls == 1 && picture.Visible, "directory selection shares the image preview and cache");
                    var types = (CheckedListBox)form.Controls.Find("fileTypes", true).Single();
                    for (var i = 0; i < types.Items.Count; i++) types.SetItemChecked(i, false);
                    Application.DoEvents();
                    check(picture.Image != null && rows.SelectedIndices.Count == 1 && bridge.Calls == 1,
                        "changing export checkboxes preserves the current resource preview");
                    var imageRow = rows.Items[imageIndex]; var titleBounds = imageRow.Bounds;
                    rows.GetType().GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Invoke(rows, new object[] { new MouseEventArgs(MouseButtons.Right, 1, titleBounds.Left + 15, titleBounds.Top + 5, 0) });
                    rows.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(i => i.Text == "复制资源路径").PerformClick();
                    check(Clipboard.GetText() == texture.Container, "directory resource path right-click copies the full logical resource path");
                    rows.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(i => i.Text == "复制相对资源路径").PerformClick();
                    check(Clipboard.GetText() == "Texture.png", "directory context menu copies a path relative to the collected root");
                    var textIndex = Enumerable.Range(0, rows.VirtualListSize).Single(i => rows.Items[i].Text == "Text.json");
                    var bounds = rows.Items[textIndex].Bounds;
                    rows.GetType().GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Invoke(rows, new object[] { new MouseEventArgs(MouseButtons.Right, 1, bounds.Left + 15, bounds.Top + 5, 0) });
                    rows.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(i => i.Text == "复制资源路径").PerformClick();
                    check(Clipboard.GetText() == textAsset.Container && rows.SelectedIndices.Count == 1,
                        "right-clicking an unselected path acts on the clicked resource");
                    PumpUntil(() => text.Visible && text.Text == "text-preview");
                    Capture(form, Path.Combine(output, "directory-text-preview.png"));
                    pages.SelectedIndex = 0;
                    Select(2); PumpUntil(() => bridge.SlowStarted);
                    var switchTimer = Stopwatch.StartNew(); Select(1);
                    check(switchTimer.ElapsedMilliseconds < 100, "selecting another asset never waits for a slow decoder");
                    PumpUntil(() => text.Visible && text.Text == "text-preview");
                    bridge.SlowRelease.SetResult(new AssetPreviewData("stale", ImagePng: bridge.Png));
                    PumpUntil(() => bridge.SlowReturned);
                    check(text.Visible && picture.Image == null && text.Text == "text-preview" && bridge.SlowCancelled,
                        "late cancelled preview cannot replace the latest selection");
                    Select(3); var calls = bridge.Calls;
                    using (var delay = Task.Delay(400)) Complete(delay);
                    check(bridge.Calls == calls && !picture.Visible && info.Text.Contains("清单信息"), "model types stay metadata-only and do not load dependencies");
                    auto.Checked = false; Select(0); Complete(Task.Delay(400));
                    check(bridge.Calls == calls && picture.Image == null, "automatic preview can be disabled without loading");
                    auto.Checked = true; PumpUntil(() => picture.Image != null);
                    Capture(form, Path.Combine(output, "search-image-preview.png"));
                    form.Size = form.MinimumSize; Capture(form, Path.Combine(output, "preview-minimum.png"));
                    check(picture.Width >= 200 && list.Height >= 100 && list.Width >= 640, "preview and result list remain usable at minimum size");
                    var loadButton = Descendants(form).OfType<Button>().Single(b => b.Text == "Load Selected");
                    check(loadButton.Parent.Controls.OfType<Button>().All(b => b.RectangleToScreen(b.ClientRectangle).Bottom <= list.RectangleToScreen(list.ClientRectangle).Top),
                        "wrapped search actions stay fully above the result list at minimum size");
                    bridge.Hold = true; Select(2); PumpUntil(() => bridge.Held);
                    var close = Stopwatch.StartNew(); form.Close();
                    check(form.IsDisposed && close.ElapsedMilliseconds < 1000, "closing never waits for pending preview work");
                    bridge.HoldRelease.SetResult(new AssetPreviewData("late")); Complete(Task.Delay(50));
                }
                catch (Exception ex) { failure = ex; }
                finally { Application.ExitThread(); }
            };
            timer.Start(); Application.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }

    private sealed class PreviewTestBridge(byte[] png) : IStudioBridge
    {
        public byte[] Png = png;
        public int Calls;
        public volatile bool SlowStarted, SlowCancelled, SlowReturned, Hold, Held;
        public TaskCompletionSource<AssetPreviewData> SlowRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AssetPreviewData> HoldRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AssetPreviewData> PreviewAsync(CatalogRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var asset = request.Assets.Single();
            if (asset.Name == "Slow")
            {
                if (Hold) { Held = true; return await HoldRelease.Task; }
                using var cancel = token.Register(() => SlowCancelled = true);
                SlowStarted = true; var result = await SlowRelease.Task; SlowReturned = true; return result;
            }
            return asset.Type == ClassIDType.TextAsset ? new("text", "text-preview") : new("192 × 128", ImagePng: Png);
        }
        public Task LoadAsync(CatalogRequest request, CancellationToken token) => Task.CompletedTask;
        public Task<string> ExportAssetsAsync(CatalogRequest request, string folder, CancellationToken token) => Task.FromResult("");
        public Task<string> ExportModelsAsync(CatalogRequest request, string folder, CancellationToken token) => Task.FromResult("");
        public Task<DirectoryExportResult> ExportDirectoryAsync(DirectoryExportPlan plan, CatalogRequest context, string folder, Action<string> report, CancellationToken token)
            => Task.FromResult(new DirectoryExportResult(folder, 0, 0, plan.Groups.Length, false));
    }

    public static void RunClosePerformance(Action<bool, string> check)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            using var owner = new Form { Opacity = 0, ShowInTaskbar = false };
            using var start = new System.Windows.Forms.Timer { Interval = 1 };
            start.Tick += (_, _) =>
            {
                start.Stop();
                try
                {
                    foreach (var rows in new[] { 10, 1_000_000, 9_000_000 })
                    {
                        using (var baseline = new Form { Opacity = 0, ShowInTaskbar = false })
                        {
                            var virtualList = new ListView { VirtualMode = true, Dock = DockStyle.Fill };
                            virtualList.RetrieveVirtualItem += (_, e) => e.Item = new ListViewItem("");
                            baseline.Controls.Add(virtualList); baseline.Show(owner); virtualList.VirtualListSize = rows;
                            var baselineClose = Stopwatch.StartNew(); baseline.Close();
                            Console.WriteLine($"BASELINE rows={rows:N0} close={baselineClose.ElapsedMilliseconds}ms");
                        }
                        var form = new ExplorerForm(new PreviewBridge(), false, new ExplorerSettings(), _ => { }) { Opacity = 0, ShowInTaskbar = false };
                        var list = (ListView)form.Controls.Find("searchResults", true).Single();
                        form.Show(owner); list.VirtualListSize = rows;
                        var close = Stopwatch.StartNew(); form.Close();
                        Console.WriteLine($"CLOSE rows={rows:N0} close={close.ElapsedMilliseconds}ms");
                        check(close.ElapsedMilliseconds < 1000, "large virtual list closes promptly: " + rows);
                        var dispose = Stopwatch.StartNew(); form.Dispose();
                        Console.WriteLine($"CLOSE rows={rows:N0} dispose={dispose.ElapsedMilliseconds}ms");
                    }
                    using var releaseSave = new ManualResetEventSlim();
                    using var saveStarted = new ManualResetEventSlim();
                    var written = new List<string>();
                    Task firstSave = null, secondSave = null;
                    using var slowSave = new ExplorerForm(new PreviewBridge(), false, new ExplorerSettings { Catalog = "first" }, snapshot =>
                    {
                        firstSave = snapshot.QueueSave(value =>
                        {
                            saveStarted.Set(); releaseSave.Wait(); written.Add(value.Catalog);
                        });
                    }) { Opacity = 0, ShowInTaskbar = false };
                    slowSave.Show(owner);
                    var closeWithSave = Stopwatch.StartNew(); slowSave.Close();
                    check(closeWithSave.ElapsedMilliseconds < 1000, "closing never waits for a slow settings write");
                    try
                    {
                        PumpUntil(() => saveStarted.IsSet);
                        var latest = ExplorerSettings.Load(); latest.Catalog = "second";
                        secondSave = latest.QueueSave(value => written.Add(value.Catalog));
                        check(ExplorerSettings.Load().Catalog == "second" && !secondSave.IsCompleted,
                            "immediate reopen uses latest settings while writes are pending");
                    }
                    finally { releaseSave.Set(); }
                    Complete(firstSave); Complete(secondSave);
                    check(written.SequenceEqual(new[] { "first", "second" }), "background settings writes preserve order across Explorer windows");
                }
                catch (Exception ex) { failure = ex; }
                finally { owner.Close(); }
            };
            start.Start(); Application.Run(owner);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
        check(true, "close performance probe completed");
    }

    public static void Run(string output, Action<bool, string> check)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware); Application.EnableVisualStyles();
            using var timer = new System.Windows.Forms.Timer { Interval = 1 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { RunOnUiThread(output, check); }
                catch (Exception ex) { failure = ex; }
                finally { Application.ExitThread(); }
            };
            timer.Start(); Application.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }

    private static void PumpUntil(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            if (timer.ElapsedMilliseconds > 10000) throw new TimeoutException("UI operation did not complete.");
            Application.DoEvents(); Thread.Sleep(5);
        }
        Application.DoEvents();
    }

    private static void Complete(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Capture(Form form, string path)
    {
        form.PerformLayout(); Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
        bitmap.Save(path);
    }

    private static void RunOnUiThread(string output, Action<bool, string> check)
    {
        var builder = new AssetIndexStore.Builder();
        builder.Add(new CatalogAsset { Name = "Hero", Type = ClassIDType.Animator, Source = "demo.blk", Container = "Assets/Characters/Hero/Model/Hero.fbx", PathID = 1 });
        builder.Add(new CatalogAsset { Name = "Idle", Type = ClassIDType.AnimationClip, Source = "demo.blk", Container = "Assets/Characters/Hero/Animation/Idle.fbx", PathID = 2 });
        builder.Add(new CatalogAsset { Name = "Walk", Type = ClassIDType.AnimationClip, Source = "demo.blk", Container = "Assets/Characters/Hero/Animation/Walk.fbx", PathID = 3 });
        builder.Add(new CatalogAsset { Name = "Diffuse", Type = ClassIDType.Texture2D, Source = "demo.blk", Container = "Assets/Characters/Hero/Textures/Diffuse.png", PathID = 4 });
        builder.Add(new CatalogAsset { Name = "Info", Type = ClassIDType.TextAsset, Source = "demo.blk", Container = "Assets/Characters/Hero/info.json", PathID = 5 });
        var catalog = builder.Build(GameType.ZZZ, "fixture");
        var catalogPath = Path.Combine(output, "ui-fixture.aex"); catalog.Save(catalogPath, default);
        var startupSettings = new ExplorerSettings { Catalog = catalogPath, WorkDirectory = output };
        using (var startup = new ExplorerForm(new PreviewBridge(), true, startupSettings, _ => { }, ex => throw new Exception("Startup failed", ex))
            { Opacity = 0, ShowInTaskbar = false })
        {
            var startupStatus = (Label)startup.Controls.Find("explorerStatus", true).Single();
            var indicator = startup.Controls.Find("catalogLoading", true).Single();
            var messages = new List<string>();
            var initialFeedback = false;
            startupStatus.TextChanged += (_, _) =>
            {
                messages.Add(startupStatus.Text);
                if (startupStatus.Text.Contains("正在加载清单 / 索引"))
                {
                    initialFeedback = indicator.Visible;
                    startup.PerformLayout();
                    using var bitmap = new Bitmap(startup.Width, startup.Height);
                    startup.DrawToBitmap(bitmap, new Rectangle(0, 0, startup.Width, startup.Height));
                    bitmap.Save(Path.Combine(output, "startup-loading.png"));
                }
            };
            startup.Show();
            var startupList = (ListView)startup.Controls.Find("searchResults", true).Single();
            PumpUntil(() => startupList.VirtualListSize == 5 && !indicator.Visible);
            check(initialFeedback, "automatic startup displays loading feedback before background read completes");
            check(messages.Any(m => m.Contains("正在读取资源索引")) && messages.Any(m => m.Contains("正在读取路径字典"))
                && messages.Any(m => m.Contains("正在应用路径字典")), "binary index and dictionary loading report their stages");
            check(startupStatus.Text.Contains("已载入 5 条资源") && !indicator.Visible,
                "startup completion removes loading indicator and reports loaded resource count");
            startup.Close();
        }
        var settings = new ExplorerSettings { WorkDirectory = output, ExportDirectory = output };
        var saves = 0;
        var bridge = new CancellationBridge();
        using var form = new ExplorerForm(bridge, false, settings, _ => saves++, ex => throw new Exception("UI operation failed", ex)) { Opacity = 0, ShowInTaskbar = false };
        form.Show();
        var pages = (TabControl)form.Controls.Find("explorerPages", true).Single();
        var directory = Descendants(form).OfType<DirectoryExportControl>().Single();
        var collect = (Button)form.Controls.Find("collectDirectory", true).Single();
        var search = (TextBox)form.Controls.Find("resourceSearch", true).Single();
        var list = (ListView)form.Controls.Find("searchResults", true).Single();
        var basket = (ListBox)form.Controls.Find("exportBasket", true).Single();
        check(pages.TabCount == 2 && !collect.Enabled, "directory page exists and requires a catalog");
        Complete(form.OpenAsync(catalogPath));
        check(list.VirtualListSize == 5, "shared catalog populates search results");
        list.Items[0].Selected = true;
        Descendants(form).OfType<Button>().Single(b => b.Text == "加入导出列表").PerformClick();
        list.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(i => i.Text == "收集/导出所在资源目录").PerformClick();
        check(pages.SelectedIndex == 1 && directory.ResourceDirectory == "Assets/Characters/Hero/Model", "search action switches page and passes selected resource directory");
        Complete(directory.CollectAsync());
        var tree = (TreeView)form.Controls.Find("directoryTree", true).Single();
        var types = (CheckedListBox)form.Controls.Find("fileTypes", true).Single();
        var preview = (ListView)form.Controls.Find("resourcePreview", true).Single();
        check(preview.VirtualListSize == 1 && Application.OpenForms.Count == 1, "directory export is embedded without opening another window");
        pages.SelectedIndex = 0;
        check(list.SelectedIndices.Count == 1 && basket.Items.Count == 1, "switching pages retains search selection and export basket");
        pages.SelectedIndex = 1;
        check(preview.VirtualListSize == 1, "returning to directory page retains collected resources");
        directory.SelectDirectory("Assets/Characters/Hero"); Complete(directory.CollectAsync());
        check(preview.VirtualListSize == 5, "directory page collects all primary resources");
        tree.Nodes[0].Nodes.Cast<TreeNode>().Single(n => n.Text == "Model").Checked = false;
        for (var i = 0; i < types.Items.Count; i++) types.SetItemChecked(i, types.Items[i].ToString().StartsWith(".fbx"));
        Application.DoEvents();
        check(preview.VirtualListSize == 5, "preview remains independent of export checkboxes");
        tree.SelectedNode = tree.Nodes[0].Nodes.Cast<TreeNode>().Single(n => n.Text == "Animation");
        check(preview.VirtualListSize == 2, "highlighting a directory scopes its resource preview");
        tree.SelectedNode = tree.Nodes[0];
        Capture(form, Path.Combine(output, "directory.png"));
        pages.SelectedIndex = 0; Capture(form, Path.Combine(output, "explorer.png"));
        form.Size = form.MinimumSize;
        pages.SelectedIndex = 1; Capture(form, Path.Combine(output, "directory-minimum.png"));
        pages.SelectedIndex = 0; Capture(form, Path.Combine(output, "explorer-minimum.png"));
        pages.SelectedIndex = 1;
        Descendants(directory).OfType<NumericUpDown>().Single().Value = 3;
        var exportTask = directory.ExportToAsync(output);
        check(directory.IsBusy && bridge.Plan.Groups.Length == 2 && bridge.Request.ExportWorkers == 3,
            "embedded export intersects directory and file type selection and snapshots worker count");
        check(bridge.Request.MeshCatalog != null && bridge.Request.ExportLogDirectory == Path.Combine(output, "ExportLogs"),
            "embedded export forwards shared catalog and log directory");
        pages.SelectedIndex = 0; search.Text = "Idle";
        PumpUntil(() => list.VirtualListSize == 1);
        check(search.Enabled && !Descendants(form).OfType<Button>().Single(b => b.Text == "Load Selected").Enabled,
            "search remains available while resource-changing actions are locked during export");
        form.Close();
        check(!form.IsDisposed && bridge.Cancelled, "closing during directory export cancels task and keeps window alive");
        bridge.Release.SetResult(); Complete(exportTask);
        check(!directory.IsBusy && collect.Enabled && saves > 0 && settings.ResourceDirectory == "Assets/Characters/Hero" && settings.ExportWorkers == 3,
            "completion restores controls and persists directory export settings");
        var replacement = new AssetIndexStore.Builder();
        replacement.Add(new CatalogAsset { Name = "New", Type = ClassIDType.TextAsset, Source = "new.blk", Container = "Assets/Characters/Hero/new.json", PathID = 6 });
        var replacementPath = Path.Combine(output, "ui-replacement.aex"); replacement.Build(GameType.ZZZ, "fixture2").Save(replacementPath, default);
        Complete(form.OpenAsync(replacementPath));
        check(tree.Nodes.Count == 0 && preview.VirtualListSize == 0, "replacing catalog invalidates old directory selections");
        pages.SelectedIndex = 1; Complete(directory.CollectAsync());
        check(preview.VirtualListSize == 1, "directory page uses replacement catalog");
        form.Close(); check(form.IsDisposed, "idle Explorer closes normally");
    }

    private sealed class CancellationBridge : IStudioBridge
    {
        public DirectoryExportPlan Plan;
        public CatalogRequest Request;
        public bool Cancelled;
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task LoadAsync(CatalogRequest request, CancellationToken token) => Task.CompletedTask;
        public Task<string> ExportAssetsAsync(CatalogRequest request, string folder, CancellationToken token) => Task.FromResult("");
        public Task<string> ExportModelsAsync(CatalogRequest request, string folder, CancellationToken token) => Task.FromResult("");
        public async Task<DirectoryExportResult> ExportDirectoryAsync(DirectoryExportPlan plan, CatalogRequest context, string folder, Action<string> report, CancellationToken token)
        {
            Plan = plan; Request = context;
            using var registration = token.Register(() => Cancelled = true);
            await Release.Task;
            return new(folder, 0, 0, plan.Groups.Length, Cancelled);
        }
    }
}
