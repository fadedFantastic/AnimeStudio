using System.Diagnostics;

namespace AnimeStudio.AssetExplorer;

internal sealed class DirectoryExportForm : Form
{
    private readonly AssetIndexStore store;
    private readonly IStudioBridge bridge;
    private readonly CatalogRequest context;
    private readonly TextBox directory = new() { Width = 680, PlaceholderText = "Assets/... 资源根目录", Name = "resourceDirectory" };
    private readonly NumericUpDown workers = new() { Minimum = 1, Maximum = 8, Value = 2, Width = 55 };
    private readonly Button collect = new() { Text = "列出目录", AutoSize = true, Name = "collectDirectory" };
    private readonly Button export = new() { Text = "导出勾选资源…", AutoSize = true };
    private readonly Button cancel = new() { Text = "取消", AutoSize = true, Enabled = false };
    private readonly Button open = new() { Text = "打开导出目录", AutoSize = true, Enabled = false };
    private readonly Button log = new() { Text = "查看导出日志", AutoSize = true, Enabled = false };
    private readonly Label summary = new() { Dock = DockStyle.Top, Height = 68, Padding = new Padding(8), Text = "输入资源根目录并点击“列出目录”，然后勾选子目录和文件类型。" };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(8), AutoEllipsis = true };
    private readonly TreeView folders = new() { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false, Name = "directoryTree" };
    private readonly CheckedListBox types = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, Name = "fileTypes" };
    private readonly ListView preview = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, VirtualMode = true, Name = "resourcePreview" };
    private DirectoryExportPlan collected, selected;
    private CancellationTokenSource operation;
    private bool updatingChecks;
    private string lastOutput, lastReport;
    public int ExportWorkers => (int)workers.Value;
    public string ResourceDirectory => directory.Text;
    public string ExportDirectory { get; private set; }

    private sealed record FileTypeChoice(string Extension, int Count)
    {
        public override string ToString() => $"{(Extension.Length == 0 ? "（无扩展名）" : Extension)}  ·  {Count:N0} 个主资源";
    }

    public DirectoryExportForm(AssetIndexStore store, IStudioBridge bridge, CatalogRequest context, string initialDirectory, string exportDirectory)
    {
        this.store = store; this.bridge = bridge; this.context = context; ExportDirectory = exportDirectory;
        Text = "按目录选择与导出资源"; Width = 1280; Height = 800; MinimumSize = new Size(1040, 620);
        StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        directory.Text = initialDirectory; workers.Value = Math.Clamp(context.ExportWorkers, 1, 8);
        var pathBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        pathBar.Controls.AddRange([directory, collect]);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        actions.Controls.AddRange([export, cancel, open, log, new Label { Text = "并发数", AutoSize = true, Padding = new Padding(8) }, workers]);
        var content = new SplitContainer { Dock = DockStyle.Fill, Width = 1200, SplitterDistance = 340, Panel1MinSize = 260, Panel2MinSize = 500 };
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        filters.RowStyles.Add(new RowStyle(SizeType.Percent, 62)); filters.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        var directoryGroup = FilterGroup("子目录（多选，勾选父目录会同步子目录）", folders, value =>
        { foreach (TreeNode node in folders.Nodes) CheckBranch(node, value); });
        var typeGroup = FilterGroup("文件类型（按原资源扩展名，多选）", types, value =>
        { for (var i = 0; i < types.Items.Count; i++) types.SetItemChecked(i, value); });
        filters.Controls.Add(directoryGroup, 0, 0); filters.Controls.Add(typeGroup, 0, 1);
        content.Panel1.Controls.Add(filters); content.Panel2.Controls.Add(preview); content.Panel2.Controls.Add(summary);
        preview.Columns.Add("相对资源路径", 590); preview.Columns.Add("处理方式", 165); preview.Columns.Add("对象数", 75);
        preview.RetrieveVirtualItem += (_, e) =>
        {
            if (selected == null || e.ItemIndex >= selected.Groups.Length) { e.Item = new ListViewItem(""); return; }
            var group = selected.Groups[e.ItemIndex];
            e.Item = new ListViewItem([group.ResourcePath[(selected.Directory.Length + 1)..], group.IsFbx ? "FBX（含子动画）" : "按可用格式导出", group.Members.Length.ToString()]);
        };
        Controls.Add(content); Controls.Add(status); Controls.Add(actions); Controls.Add(pathBar);
        collect.Click += async (_, _) => await CollectAsync();
        export.Click += async (_, _) => await ExportAsync();
        cancel.Click += (_, _) => operation?.Cancel();
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
        FormClosing += (_, e) => { if (operation != null) { operation.Cancel(); e.Cancel = true; Status("正在取消；任务结束后可关闭窗口。"); } };
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
        try { preview.VirtualListSize = 0; collected = selected = null; folders.Nodes.Clear(); types.Items.Clear(); }
        finally { updatingChecks = false; }
        summary.Text = "根目录已变化，请重新列出目录。";
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
            folders.Sort(); root.Expand();
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
        preview.VirtualListSize = selected.Groups.Length; preview.Invalidate();
        summary.Text = $"已选 {selected.Groups.Length:N0} / {collected.Groups.Length:N0} 个主资源 · {selected.Groups.Sum(g => g.Members.Length):N0} 个对象" +
            "\n输出位置对应输入根目录，保留相对目录；FBX 子动画与所需贴图一起导出。";
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation != null) return;
        using var cts = new CancellationTokenSource(); operation = cts;
        workers.Enabled = directory.Enabled = collect.Enabled = export.Enabled = folders.Parent.Enabled = types.Parent.Enabled = false; cancel.Enabled = true;
        try { await action(cts.Token); }
        catch (OperationCanceledException) { Status("任务已取消。"); }
        catch (Exception ex) { Status("失败：" + ex.Message); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { operation = null; workers.Enabled = directory.Enabled = collect.Enabled = export.Enabled = folders.Parent.Enabled = types.Parent.Enabled = true; cancel.Enabled = false; }
    }

    private Task CollectAsync()
    {
        var path = directory.Text;
        return RunAsync(async token =>
        {
            Status("正在读取根目录下的资源与子目录…");
            var result = await Task.Run(() => store.CollectDirectory(path, true, token), token);
            collected = result; PopulateFilters();
            Status(result.Groups.Length == 0 ? "没有匹配项，请确认清单中的真实资源路径。" : "勾选子目录和文件类型后导出；已有文件及不支持的复合资源会跳过并记入日志。");
        });
    }

    private async Task ExportAsync()
    {
        if (collected == null) await CollectAsync();
        if (operation != null || collected == null) return;
        UpdateSelection();
        if (selected.Groups.Length == 0) { Status("请至少勾选一个有匹配资源的目录和文件类型。"); return; }
        using var dialog = new FolderBrowserDialog { Description = "选择与资源根目录对应的输出位置（按相对路径直接保存）", UseDescriptionForTitle = true, SelectedPath = ExportDirectory };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ExportDirectory = dialog.SelectedPath;
        var snapshot = selected;
        await RunAsync(async token =>
        {
            var result = await bridge.ExportDirectoryAsync(snapshot, context with { ExportWorkers = ExportWorkers }, ExportDirectory, Status, token);
            lastOutput = result.OutputDirectory; lastReport = result.ReportPath;
            open.Enabled = Directory.Exists(lastOutput); log.Enabled = File.Exists(lastReport);
            Status($"{(result.Cancelled ? "已取消" : "导出结束")}：成功 {result.Succeeded}，跳过 {result.Skipped}，失败 {result.Failed}，未完成 {result.Pending}。日志：{lastReport}");
        });
    }

    private void Status(string message)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(() => Status(message)); return; }
        status.Text = message;
    }
}
