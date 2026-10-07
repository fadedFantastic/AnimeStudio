using System.Diagnostics;
using Newtonsoft.Json;

namespace AnimeStudio.AssetExplorer;

public sealed class ExplorerForm : Form
{
    private readonly IStudioBridge bridge;
    private readonly ExplorerSettings settings;
    private readonly TextBox search = new() { Width = 360, PlaceholderText = "资源名 / 路径 / blk（忽略大小写）" };
    private readonly TextBox block = new() { Width = 160, PlaceholderText = "限定 blk" };
    private readonly ComboBox type = new() { Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox game = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox regex = new() { Text = "正则", AutoSize = true };
    private readonly CheckBox full = new() { Text = "包含内部组件", AutoSize = true };
    private readonly TextBox root = new() { Dock = DockStyle.Fill };
    private readonly TextBox cab = new() { Dock = DockStyle.Fill };
    private readonly TextBox dictionary = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 45, AutoEllipsis = true, Padding = new Padding(8), Text = "打开清单或建立索引" };
    private readonly Label count = new() { AutoSize = true, Padding = new Padding(8) };
    private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, VirtualMode = true, FullRowSelect = true, HideSelection = false, MultiSelect = true };
    private readonly ListBox basketView = new() { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended };
    private readonly List<CatalogAsset> basket = [];
    private readonly List<Control> busyControls = [];
    private readonly System.Windows.Forms.Timer debounce = new() { Interval = 200 };
    private readonly Button stop = new() { Text = "取消", AutoSize = true, Enabled = false };
    private AssetIndexStore store;
    private int[] view = [];
    private CancellationTokenSource operation, query;
    private bool busy;

    public ExplorerForm(IStudioBridge bridge, bool autoLoadLast = true)
    {
        this.bridge = bridge;
        settings = ExplorerSettings.Load();
        Text = "Asset Explorer · 快速搜索与模型导出";
        Width = 1280; Height = 850; MinimumSize = new Size(940, 640);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        BuildUi();
        root.Text = settings.SourceRoot; cab.Text = settings.CabMap; dictionary.Text = settings.DictionaryDirectory;
        game.Items.AddRange(Enum.GetNames<GameType>()); game.SelectedItem = settings.Game.ToString(); full.Checked = settings.Full;
        Shown += async (_, _) => { if (autoLoadLast && File.Exists(settings.Catalog)) await OpenAsync(settings.Catalog); };
        FormClosing += (_, e) =>
        {
            if (busy) { operation?.Cancel(); e.Cancel = true; Status("正在取消任务；完成后可关闭窗口。"); return; }
            query?.Cancel(); SaveSettings(); debounce.Dispose();
        };
    }

    private Button Button(string text, Func<Task> action, bool disableWhileBusy = true)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(3) };
        button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { Error(ex); } };
        if (disableWhileBusy) busyControls.Add(button);
        return button;
    }
    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = true };
    private void BuildUi()
    {
        var header = Flow();
        header.Controls.Add(Button("打开清单…", async () =>
        {
            using var dialog = new OpenFileDialog { Filter = "资源清单|*.aex;*.json;*.tsv;*.map", FileName = settings.Catalog };
            if (dialog.ShowDialog(this) == DialogResult.OK) await OpenAsync(dialog.FileName);
        }));
        header.Controls.Add(Button("建立 / 更新索引", BuildIndexAsync));
        header.Controls.Add(game); header.Controls.Add(full);
        header.Controls.Add(Button("补全路径", RecoverAsync));
        header.Controls.Add(stop);
        stop.Click += (_, _) => operation?.Cancel();

        var config = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(8) };
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddPath("资源根目录", root, false); AddPath("CABMap", cab, true); AddPath("路径字典目录", dictionary, false);
        void AddPath(string label, TextBox box, bool file)
        {
            var row = config.RowCount++;
            config.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
            config.Controls.Add(box, 1, row);
            config.Controls.Add(Button("浏览…", () =>
            {
                if (file)
                { using var dialog = new OpenFileDialog { Filter = "CABMap|*.bin" }; if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName; }
                else
                { using var dialog = new FolderBrowserDialog { SelectedPath = box.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath; }
                return Task.CompletedTask;
            }), 2, row);
            busyControls.Add(box);
        }
        var filters = Flow();
        filters.Controls.AddRange([search, type, block, regex, count]);
        type.Items.Add("全部类型"); type.SelectedIndex = 0;
        search.TextChanged += (_, _) => ScheduleSearch(); block.TextChanged += (_, _) => ScheduleSearch();
        type.SelectedIndexChanged += (_, _) => ScheduleSearch(); regex.CheckedChanged += (_, _) => ScheduleSearch();
        debounce.Tick += async (_, _) => { debounce.Stop(); await SearchAsync(); };
        foreach (var column in new[] { ("名称", 230), ("类型", 125), ("blk", 150), ("路径（~ 表示推测）", 460), ("CAB", 250), ("Offset", 100), ("PathID", 165) })
            list.Columns.Add(column.Item1, column.Item2);
        list.RetrieveVirtualItem += (_, e) =>
        {
            if (store == null || e.ItemIndex >= view.Length) { e.Item = new ListViewItem(""); return; }
            var i = view[e.ItemIndex];
            e.Item = new ListViewItem([store.Name[i], store.Type[i], store.Blk[i], store.Display(i), store.Cab[i], store.Offset[i].ToString(), store.PathID[i].ToString()]);
        };
        list.DoubleClick += (_, _) => AddToBasket();
        var menu = new ContextMenuStrip();
        menu.Items.Add("加入导出列表", null, (_, _) => AddToBasket());
        menu.Items.Add("复制资源路径", null, (_, _) => CopySelected(a => a.Container));
        menu.Items.Add("复制 blk 完整路径", null, (_, _) => CopySelected(a => a.Source));
        menu.Items.Add("收集/导出所在资源目录…", null, (_, _) => OpenDirectoryExport());
        list.ContextMenuStrip = menu;
        var actions = Flow();
        actions.Controls.Add(Button("加入导出列表", () => { AddToBasket(); return Task.CompletedTask; }));
        actions.Controls.Add(Button("Load Selected", () => PerformAsync("load")));
        actions.Controls.Add(Button("导出选中资源…", () => PerformAsync("assets")));
        actions.Controls.Add(Button("一键导出 FBX + 动画 + 贴图…", () => PerformAsync("models")));
        actions.Controls.Add(Button("目录批量导出…", () => { OpenDirectoryExport(); return Task.CompletedTask; }));
        var basketPanel = new Panel { Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(8) };
        var basketBar = Flow();
        basketBar.Controls.Add(new Label { Text = "导出列表（跨搜索保留；非空时优先使用此列表）", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        basketBar.Controls.Add(Button("移除选中", () =>
        { foreach (var i in basketView.SelectedIndices.Cast<int>().OrderByDescending(i => i)) basket.RemoveAt(i); RefreshBasket(); return Task.CompletedTask; }));
        basketBar.Controls.Add(Button("清空", () => { basket.Clear(); RefreshBasket(); return Task.CompletedTask; }));
        basketPanel.Controls.Add(basketView); basketPanel.Controls.Add(basketBar);
        var top = new Panel { Dock = DockStyle.Top, AutoSize = true };
        top.Controls.Add(actions); top.Controls.Add(filters); top.Controls.Add(config); top.Controls.Add(header);
        Controls.Add(list); Controls.Add(basketPanel); Controls.Add(status); Controls.Add(top);
        busyControls.AddRange([game, full, search, type, block, regex, list, basketView]);
    }

    private void ScheduleSearch() { query?.Cancel(); debounce.Stop(); if (!busy) debounce.Start(); }
    private async Task SearchAsync()
    {
        if (store == null || busy) return;
        query?.Cancel();
        var current = new CancellationTokenSource(); query = current;
        var snapshot = store;
        var request = new AssetIndexStore.Query(search.Text, type.Text, block.Text, regex.Checked);
        var timer = Stopwatch.StartNew();
        try
        {
            var results = await Task.Run(() => snapshot.Search(request, current.Token), current.Token);
            if (current != query || current.IsCancellationRequested || IsDisposed) return;
            list.VirtualListSize = 0; view = results; list.VirtualListSize = view.Length; list.Invalidate();
            count.Text = $"{view.Length:N0} / {store.Count:N0} 条 · {timer.ElapsedMilliseconds} ms";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (query == current) Status("搜索失败：" + ex.GetBaseException().Message); }
        finally { current.Dispose(); if (query == current) query = null; }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (busy) return;
        busy = true; query?.Cancel(); debounce.Stop();
        foreach (var control in busyControls) control.Enabled = false;
        stop.Enabled = true;
        using var cts = new CancellationTokenSource(); operation = cts;
        try { await action(cts.Token); }
        catch (OperationCanceledException) { Status("任务已取消；已完成的扫描缓存和导出文件保留。"); }
        catch (Exception ex) { Error(ex); }
        finally
        {
            operation = null; busy = false; stop.Enabled = false;
            foreach (var control in busyControls) control.Enabled = true;
            await SearchAsync();
        }
    }

    private Task OpenAsync(string path) => RunAsync(async token =>
    {
        var loaded = await Task.Run(() => AssetIndexStore.Load(path, Path.Combine(settings.WorkDirectory, "imports"), Status, token), token);
        var dictionaryPath = dictionary.Text;
        var paths = await Task.Run(() => LoadDictionary(dictionaryPath), token);
        await Task.Run(() => loaded.ApplyPathDict(paths), token);
        store = loaded; view = []; list.VirtualListSize = 0;
        basket.Clear(); RefreshBasket();
        type.Items.Clear(); type.Items.AddRange(store.AllTypes); type.SelectedIndex = 0;
        game.SelectedItem = store.Game.ToString();
        var discoveredCab = ExplorerSettings.FindCabMap(path);
        if (string.IsNullOrEmpty(discoveredCab)) discoveredCab = ExplorerSettings.FindCabMap(store.SourcePath);
        if (!string.IsNullOrEmpty(discoveredCab) || !string.Equals(settings.Catalog, path, StringComparison.OrdinalIgnoreCase)) cab.Text = discoveredCab;
        settings.Catalog = path;
        SaveSettings(); Status($"已载入 {store.Count:N0} 条资源。选择模型，直接一键导出；可把额外动画加入导出列表。");
    });

    private Task BuildIndexAsync() => RunAsync(async token =>
    {
        SaveSettings();
        var source = root.Text;
        if (Directory.Exists(Path.Combine(source, "Blocks"))) source = Path.Combine(source, "Blocks");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("请选择游戏的 Blocks 目录。");
        root.Text = source;
        var id = Guid.NewGuid().ToString("N");
        var result = await IndexWorker.StartAsync(new(source, settings.WorkDirectory, settings.Game, full.Checked,
            Path.Combine(settings.WorkDirectory, id + ".cancel"), Path.Combine(settings.WorkDirectory, id + ".result.json")), Status, token);
        store = await Task.Run(() => AssetIndexStore.Load(result.Catalog, "", Status, token), token);
        var dictionaryPath = dictionary.Text;
        await Task.Run(() => store.ApplyPathDict(LoadDictionary(dictionaryPath)), token);
        settings.Catalog = result.Catalog; cab.Text = Path.ChangeExtension(result.Catalog, ".bin");
        basket.Clear(); RefreshBasket(); list.VirtualListSize = 0; view = [];
        type.Items.Clear(); type.Items.AddRange(store.AllTypes); type.SelectedIndex = 0;
        SaveSettings();
        Status($"索引完成：新扫 {result.Scanned:N0}，复用 {result.Reused:N0}，失败 {result.Failed:N0}。" +
            (result.Failed > 0 ? "详情见索引旁的 scan-errors.txt；更新索引可重试。" : ""));
    });

    private Task RecoverAsync() => RunAsync(async token =>
    {
        if (store == null) throw new InvalidOperationException("请先打开清单。");
        var dir = dictionary.Text;
        var result = await Task.Run(() =>
        {
            var known = LoadDictionary(dir); store.ApplyPathDict(known);
            var request = new PathRecovery.Request { Name = store.Name, Type = store.Type, Container = store.Container,
                Blk = store.Blk, Count = store.Count, KnownPaths = known, DeepScan = false };
            var recovered = PathRecovery.Recover(request, Status, token);
            store.Guessed ??= new string[store.Count];
            var guessed = PathRecovery.GuessDirectories(request, store.Guessed, Status, token);
            var output = Path.Combine(settings.WorkDirectory, "recovered.aex");
            store.Save(output, token);
            return (recovered.SolvedHashes, guessed, output);
        }, token);
        settings.Catalog = result.output; SaveSettings(); list.Invalidate();
        Status($"确证补全 {result.SolvedHashes:N0} 个路径；{result.guessed:N0} 条显示推测目录（~）。");
    });

    private async Task PerformAsync(string mode)
    {
        if (store == null) return;
        var selected = basket.Count > 0 ? basket.ToArray() : Selected();
        if (selected.Length == 0) { Status("请先选择资源或加入导出列表。"); return; }
        var request = new CatalogRequest(Enum.Parse<GameType>(game.Text), selected, cab.Text, root.Text) { MeshCatalog = store };
        var output = settings.ExportDirectory;
        if (mode != "load")
        {
            using var dialog = new FolderBrowserDialog { Description = "选择导出目录", SelectedPath = output, UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            output = dialog.SelectedPath; settings.ExportDirectory = output;
        }
        SaveSettings();
        var loaded = false;
        await RunAsync(async token =>
        {
            Status("解析依赖并加载资源…");
            if (mode == "load") { await bridge.LoadAsync(request, token); loaded = true; Status("已载入 Studio，可在 Asset List 和 Scene Hierarchy 中继续操作。"); }
            else Status(await (mode == "models" ? bridge.ExportModelsAsync(request, output, token) : bridge.ExportAssetsAsync(request, output, token)));
        });
        if (loaded) DialogResult = DialogResult.OK;
    }
    private CatalogAsset[] Selected() => store == null ? [] : list.SelectedIndices.Cast<int>().Where(i => i < view.Length).Select(i => store.Entry(view[i])).ToArray();
    private void OpenDirectoryExport()
    {
        if (busy) return;
        if (store == null) { Status("请先打开资源清单。"); return; }
        var selectedDirectory = ResourcePaths.Parent(Selected().FirstOrDefault()?.Container);
        var context = new CatalogRequest(Enum.Parse<GameType>(game.Text), [], cab.Text, root.Text) { MeshCatalog = store, ExportWorkers = settings.ExportWorkers };
        using var dialog = new DirectoryExportForm(store, bridge, context,
            string.IsNullOrEmpty(selectedDirectory) ? settings.ResourceDirectory : selectedDirectory, settings.ExportDirectory);
        dialog.ShowDialog(this);
        settings.ExportWorkers = dialog.ExportWorkers; settings.ResourceDirectory = dialog.ResourceDirectory; settings.ExportDirectory = dialog.ExportDirectory; SaveSettings();
    }
    private void AddToBasket() { if (busy) return; foreach (var a in Selected()) if (!basket.Contains(a)) basket.Add(a); RefreshBasket(); }
    private void RefreshBasket() { basketView.Items.Clear(); basketView.Items.AddRange(basket.Select(a => $"{a.Type}  {a.Name}  · {Path.GetFileName(a.Source)} @ {a.Offset}").ToArray()); }
    private void CopySelected(Func<CatalogAsset, string> get) { var text = string.Join(Environment.NewLine, Selected().Select(get)); if (text.Length > 0) Clipboard.SetText(text); }
    private static Dictionary<ulong, string> LoadDictionary(string directory)
    {
        var result = new Dictionary<ulong, string>();
        if (!Directory.Exists(directory)) return result;
        foreach (var name in new[] { "Z3-AssetIndex-Eleiyas.json", "Z3-AssetIndex-Recovered.json" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) foreach (var entry in JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(path))) result[entry.Key] = entry.Value;
        }
        return result;
    }
    private void SaveSettings()
    {
        settings.SourceRoot = root.Text; settings.CabMap = cab.Text; settings.DictionaryDirectory = dictionary.Text;
        settings.Game = Enum.TryParse<GameType>(game.Text, out var selected) ? selected : GameType.ZZZ; settings.Full = full.Checked; settings.Save();
    }
    private void Status(string message)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(() => Status(message)); return; }
        status.Text = message;
    }
    private void Error(Exception ex) { Status("失败：" + ex.GetBaseException().Message); MessageBox.Show(this, ex.GetBaseException().Message, "Asset Explorer", MessageBoxButtons.OK, MessageBoxIcon.Error); }
}
