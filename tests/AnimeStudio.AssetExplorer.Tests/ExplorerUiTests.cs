using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using AnimeStudio;
using AnimeStudio.AssetExplorer;

internal static class ExplorerUiTests
{
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
