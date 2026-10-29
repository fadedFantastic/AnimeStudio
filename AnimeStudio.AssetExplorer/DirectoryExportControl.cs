using System.Diagnostics;

namespace AnimeStudio.AssetExplorer;

internal sealed class DirectoryExportControl : UserControl
{
    private AssetIndexStore store;
    private readonly IStudioBridge bridge;
    private readonly Func<CatalogRequest> getContext;
    private readonly TextBox directory = new() { Width = 680, PlaceholderText = "Assets/... 资源根目录", Name = "resourceDirectory" };
    private readonly NumericUpDown workers = new() { Minimum = 1, Maximum = 8, Value = 2, Width = 55 };
    private readonly Button collect = new() { Text = "列出目录", AutoSize = true, Name = "collectDirectory" };
    private readonly Button export = new() { Text = "导出勾选资源…", AutoSize = true };
    private readonly Button open = new() { Text = "打开导出目录", AutoSize = true, Enabled = false };
    private readonly Button log = new() { Text = "查看导出日志", AutoSize = true, Enabled = false };
    private readonly Label summary = new() { Dock = DockStyle.Top, Height = 82, Padding = new Padding(8), Text = "输入资源根目录并点击“列出目录”，然后勾选子目录和文件类型。" };
    private readonly TreeView folders = new() { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false, Name = "directoryTree" };
    private readonly CheckedListBox types = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, Name = "fileTypes" };
    private readonly ListView preview = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, VirtualMode = true, Name = "resourcePreview" };
    private DirectoryExportPlan collected, selected;
    private PrimaryAssetGroup[] visible = [];
    private string visibleRoot = "";
    private CancellationTokenSource operation;
    private bool updatingChecks;
    private bool available = true;
    private string lastOutput, lastReport;
    public bool IsBusy => operation != null;
    public event Action<bool> BusyChanged;
    public event Action<string> StatusChanged;
    public event Action<Exception> ErrorOccurred;
    public event Action<CatalogAsset> PreviewSelectionChanged;
    public CatalogAsset PreviewAsset
    {
        get
        {
            if (preview.SelectedIndices.Count == 0) return null;
            var index = preview.FocusedItem is { Selected: true } focused ? focused.Index : preview.SelectedIndices[0];
            return index >= 0 && index < visible.Length ? AssetPreview.Representative(visible[index]) : null;
        }
    }
    public int ExportWorkers => (int)workers.Value;
    public string ResourceDirectory => directory.Text;
    public string ExportDirectory { get; private set; }

    private sealed record FileTypeChoice(string Extension, int Count)
    {
        public override string ToString() => $"{(Extension.Length == 0 ? "（无扩展名）" : Extension)}  ·  {Count:N0} 个主资源";
    }

    public DirectoryExportControl(IStudioBridge bridge, Func<CatalogRequest> getContext, string initialDirectory, string exportDirectory, int exportWorkers)
    {
        this.bridge = bridge; this.getContext = getContext; ExportDirectory = exportDirectory;
        Dock = DockStyle.Fill; Size = new Size(1200, 600); AutoScaleMode = AutoScaleMode.Dpi;
        directory.Text = initialDirectory; workers.Value = Math.Clamp(exportWorkers, 1, 8);
        var pathBar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), ColumnCount = 2 };
        pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        directory.Dock = DockStyle.Fill; pathBar.Controls.Add(directory, 0, 0); pathBar.Controls.Add(collect, 1, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        actions.Controls.AddRange([export, open, log, new Label { Text = "并发数", AutoSize = true, Padding = new Padding(8) }, workers]);
        var content = new SplitContainer { Dock = DockStyle.Fill, Width = 1200, SplitterDistance = 260, Panel1MinSize = 220, Panel2MinSize = 420 };
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        filters.RowStyles.Add(new RowStyle(SizeType.Percent, 62)); filters.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        var directoryGroup = FilterGroup("子目录（勾选父目录同步子目录）", folders, value =>
        { foreach (TreeNode node in folders.Nodes) CheckBranch(node, value); });
        var typeGroup = FilterGroup("文件类型（原资源扩展名，多选）", types, value =>
        { for (var i = 0; i < types.Items.Count; i++) types.SetItemChecked(i, value); });
        filters.Controls.Add(directoryGroup, 0, 0); filters.Controls.Add(typeGroup, 0, 1);
        content.Panel1.Controls.Add(filters); content.Panel2.Controls.Add(preview); content.Panel2.Controls.Add(summary);
        preview.Columns.Add("相对资源路径", 390); preview.Columns.Add("处理方式", 165); preview.Columns.Add("对象数", 75);
        preview.RetrieveVirtualItem += (_, e) =>
        {
            if (e.ItemIndex >= visible.Length) { e.Item = new ListViewItem(""); return; }
            var group = visible[e.ItemIndex];
            e.Item = new ListViewItem([group.ResourcePath[(visibleRoot.Length + 1)..], group.IsFbx ? "FBX（含子动画）" :
                group.IsCompiledController ? "Unity 可编辑状态机（保留编译数据）" : "按可用格式导出", group.Members.Length.ToString()]);
        };
        preview.SelectedIndexChanged += (_, _) => PreviewSelectionChanged?.Invoke(PreviewAsset);
        preview.MouseUp += (_, _) => PreviewSelectionChanged?.Invoke(PreviewAsset);
        preview.KeyUp += (_, _) => PreviewSelectionChanged?.Invoke(PreviewAsset);
        var menu = new ContextMenuStrip();
        menu.Items.Add("复制资源路径", null, (_, _) => CopyGroups(g => g.ResourcePath));
        menu.Items.Add("复制相对资源路径", null, (_, _) => CopyGroups(g => g.ResourcePath[(collected.Directory.Length + 1)..]));
        menu.Items.Add("复制 blk 完整路径", null, (_, _) => CopyGroups(g => string.Join(Environment.NewLine, g.Members.Select(a => a.Source).Distinct(StringComparer.OrdinalIgnoreCase))));
        menu.Items.Add("定位所在资源目录", null, (_, _) =>
        {
            var path = ResourcePaths.Parent(SelectedGroups().FirstOrDefault()?.ResourcePath);
            var node = Nodes(folders.Nodes).FirstOrDefault(n => string.Equals(n.Tag as string, path, StringComparison.OrdinalIgnoreCase));
            if (node != null) { folders.SelectedNode = node; node.EnsureVisible(); }
        });
        menu.Opening += (_, e) => e.Cancel = preview.SelectedIndices.Count == 0;
        preview.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = preview.HitTest(e.Location).Item;
            if (hit == null) { preview.SelectedIndices.Clear(); return; }
            if (!hit.Selected) { preview.SelectedIndices.Clear(); hit.Selected = true; }
            hit.Focused = true;
            PreviewSelectionChanged?.Invoke(PreviewAsset);
        };
        preview.ContextMenuStrip = menu;
        Controls.Add(content); Controls.Add(actions); Controls.Add(pathBar);
        collect.Click += async (_, _) => await CollectAsync();
        export.Click += async (_, _) => await ExportAsync();
        open.Click += (_, _) => { if (Directory.Exists(lastOutput)) Process.Start(new ProcessStartInfo(lastOutput) { UseShellExecute = true }); };
        log.Click += (_, _) => { if (File.Exists(lastReport)) Process.Start(new ProcessStartInfo(lastReport) { UseShellExecute = true }); };
        directory.TextChanged += (_, _) => InvalidatePlan();
        folders.AfterCheck += (_, e) =>
        {
            if (updatingChecks) return;
            updatingChecks = true;
            try { foreach (TreeNode child in e.Node.Nodes) CheckBranch(child, e.Node.Checked); }
            finally { updatingChecks = false; }
            UpdateSelection();
        };
        types.ItemCheck += (_, _) => { if (!updatingChecks && IsHandleCreated) BeginInvoke(() => UpdateSelection()); };
        // Highlighting a folder narrows the preview to that folder; the checkboxes still decide what gets exported.
        folders.AfterSelect += (_, e) => { if (!updatingChecks && e.Node != null) UpdateSelection(); };
        UpdateAvailability();
    }

    public void SetCatalog(AssetIndexStore catalog, bool invalidate = false)
    {
        if (IsBusy) throw new InvalidOperationException("请等待目录任务结束后再更换清单。");
        if (!ReferenceEquals(store, catalog) || invalidate)
        {
            store = catalog;
            InvalidatePlan();
            summary.Text = catalog == null ? "请先打开资源清单。" : "输入资源根目录并点击“列出目录”，然后勾选子目录和文件类型。";
        }
        UpdateAvailability();
    }

    public void SelectDirectory(string path)
    {
        if (!IsBusy && !string.IsNullOrWhiteSpace(path) && !string.Equals(directory.Text, path, StringComparison.OrdinalIgnoreCase))
            directory.Text = path;
    }

    public void SetExportDirectory(string path) => ExportDirectory = path;
    public void CancelOperation() => operation?.Cancel();
    public void SetAvailable(bool value) { available = value; UpdateAvailability(); }

    private void UpdateAvailability()
    {
        workers.Enabled = directory.Enabled = collect.Enabled = export.Enabled = folders.Parent.Enabled = types.Parent.Enabled =
            available && store != null && !IsBusy;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { CancelOperation(); PrepareClose(); }
        base.Dispose(disposing);
    }

    public void PrepareClose()
    {
        if (!preview.IsDisposed) preview.VirtualListSize = 0;
        store = null; collected = selected = null; visible = [];
    }

    private GroupBox FilterGroup(string title, Control control, Action<bool> setAll)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34 };
        foreach (var (text, value) in new[] { ("全选", true), ("全不选", false) })
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (_, _) =>
            {
                if (operation != null) return;
                updatingChecks = true;
                try { setAll(value); }
                finally { updatingChecks = false; }
                UpdateSelection();
            };
            buttons.Controls.Add(button);
        }
        group.Controls.Add(control); group.Controls.Add(buttons);
        return group;
    }

    private static void CheckBranch(TreeNode node, bool value)
    { node.Checked = value; foreach (TreeNode child in node.Nodes) CheckBranch(child, value); }

    private static IEnumerable<TreeNode> Nodes(TreeNodeCollection nodes)
    { foreach (TreeNode node in nodes) { yield return node; foreach (var child in Nodes(node.Nodes)) yield return child; } }

    private void InvalidatePlan()
    {
        updatingChecks = true;
        try { preview.VirtualListSize = 0; collected = selected = null; visible = []; visibleRoot = ""; folders.Nodes.Clear(); types.Items.Clear(); }
        finally { updatingChecks = false; }
        summary.Text = "根目录已变化，请重新列出目录。";
        PreviewSelectionChanged?.Invoke(null);
    }

    private void PopulateFilters()
    {
        updatingChecks = true; folders.BeginUpdate(); types.BeginUpdate();
        try
        {
            folders.Nodes.Clear(); types.Items.Clear();
            var root = new TreeNode(Path.GetFileName(collected.Directory) + "（根目录）") { Tag = collected.Directory, Checked = true };
            folders.Nodes.Add(root);
            var nodes = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase) { [collected.Directory] = root };
            foreach (var group in collected.Groups)
            {
                var relative = group.ResourcePath[(collected.Directory.Length + 1)..].Split('/');
                var path = collected.Directory; var parent = root;
                foreach (var segment in relative.SkipLast(1))
                {
                    path += "/" + segment;
                    if (!nodes.TryGetValue(path, out var node))
                    {
                        node = new TreeNode(segment) { Tag = path, Checked = true }; nodes.Add(path, node); parent.Nodes.Add(node);
                    }
                    parent = node;
                }
            }
            folders.Sort(); root.Expand(); folders.SelectedNode = root;
            foreach (var type in collected.Groups.GroupBy(g => g.Extension).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                types.Items.Add(new FileTypeChoice(type.Key, type.Count()), true);
        }
        finally { folders.EndUpdate(); types.EndUpdate(); updatingChecks = false; }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        if (collected == null || updatingChecks || IsDisposed) return;
        selected = collected.Select(Nodes(folders.Nodes).Where(n => n.Checked).Select(n => (string)n.Tag),
            types.CheckedItems.Cast<FileTypeChoice>().Select(t => t.Extension));
        var scope = folders.SelectedNode?.Tag as string ?? collected.Directory;
        if (!string.Equals(visibleRoot, scope, StringComparison.OrdinalIgnoreCase))
        {
            preview.VirtualListSize = 0; visibleRoot = scope; visible = collected.Within(scope);
            preview.VirtualListSize = visible.Length; preview.Invalidate(); PreviewSelectionChanged?.Invoke(null);
        }
        var scopeLabel = string.Equals(scope, collected.Directory, StringComparison.OrdinalIgnoreCase) ? "整个搜索目录" : scope[(collected.Directory.Length + 1)..];
        summary.Text = $"预览范围：{scopeLabel} · 当前目录 {visible.Length:N0} 个主资源" +
            $"\n已选 {selected.Groups.Length:N0} / {collected.Groups.Length:N0} 个主资源 · {selected.Groups.Sum(g => g.Members.Length):N0} 个对象" +
            "\n输出位置对应输入根目录，保留相对目录；FBX 子动画与所需贴图一起导出。";
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || !available || store == null) return;
        using var cts = new CancellationTokenSource(); operation = cts;
        UpdateAvailability(); BusyChanged?.Invoke(true);
        try { await action(cts.Token); }
        catch (OperationCanceledException) { Status("任务已取消。"); }
        catch (Exception ex) { Status("失败：" + ex.Message); if (!IsDisposed) ErrorOccurred?.Invoke(ex); }
        finally { operation = null; if (!IsDisposed) { UpdateAvailability(); BusyChanged?.Invoke(false); } }
    }

    internal Task CollectAsync()
    {
        var path = directory.Text;
        return RunAsync(async token =>
        {
            Status("正在读取根目录下的资源与子目录…");
            var result = await Task.Run(() => store.CollectDirectory(path, true, token), token);
            token.ThrowIfCancellationRequested();
            preview.VirtualListSize = 0; visibleRoot = ""; visible = [];
            collected = result; PopulateFilters();
            Status(result.Groups.Length == 0 ? "没有匹配项，请确认清单中的真实资源路径。" : "勾选子目录和文件类型后导出；已有文件及不支持的复合资源会跳过并记入日志。");
        });
    }

    private async Task ExportAsync()
    {
        if (IsBusy || !available || store == null) return;
        if (collected == null) await CollectAsync();
        if (operation != null || collected == null) return;
        UpdateSelection();
        if (selected.Groups.Length == 0) { Status("请至少勾选一个有匹配资源的目录和文件类型。"); return; }
        using var dialog = new FolderBrowserDialog { Description = "选择与资源根目录对应的输出位置（按相对路径直接保存）", UseDescriptionForTitle = true, SelectedPath = ExportDirectory };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        await ExportToAsync(dialog.SelectedPath);
    }

    internal Task ExportToAsync(string output)
    {
        if (IsBusy || !available || selected == null || selected.Groups.Length == 0) return Task.CompletedTask;
        ExportDirectory = output;
        var snapshot = selected;
        var context = getContext() with { ExportWorkers = ExportWorkers };
        return RunAsync(async token =>
        {
            var result = await bridge.ExportDirectoryAsync(snapshot, context, ExportDirectory, Status, token);
            if (IsDisposed) return;
            lastOutput = result.OutputDirectory; lastReport = result.ReportPath;
            open.Enabled = Directory.Exists(lastOutput); log.Enabled = File.Exists(lastReport);
            Status($"{(result.Cancelled ? "已取消" : "导出结束")}：成功 {result.Succeeded}，跳过 {result.Skipped}，失败 {result.Failed}，未完成 {result.Pending}。日志：{lastReport}");
        });
    }

    private void Status(string message)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(() => Status(message)); return; }
        StatusChanged?.Invoke(message);
    }

    private PrimaryAssetGroup[] SelectedGroups() => preview.SelectedIndices.Cast<int>().Where(i => i >= 0 && i < visible.Length).Select(i => visible[i]).ToArray();
    private void CopyGroups(Func<PrimaryAssetGroup, string> get)
    {
        var value = string.Join(Environment.NewLine, SelectedGroups().Select(get));
        if (value.Length > 0) Clipboard.SetText(value);
    }
}
