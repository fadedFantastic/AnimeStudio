using System.Diagnostics;

namespace AnimeStudio.AssetExplorer;

internal sealed class DirectoryExportForm : Form
{
    private readonly AssetIndexStore store;
    private readonly IStudioBridge bridge;
    private readonly CatalogRequest context;
    private readonly TextBox directory = new() { Width = 590, PlaceholderText = "Assets/... 资源目录" };
    private readonly CheckBox recursive = new() { Text = "包含子目录", Checked = true, AutoSize = true };
    private readonly NumericUpDown workers = new() { Minimum = 1, Maximum = 8, Value = 2, Width = 55 };
    public int ExportWorkers => (int)workers.Value;
    private readonly Button collect = new() { Text = "收集目录", AutoSize = true };
    private readonly Button export = new() { Text = "一键导出目录…", AutoSize = true };
    private readonly Button cancel = new() { Text = "取消", AutoSize = true, Enabled = false };
    private readonly Button open = new() { Text = "打开导出目录", AutoSize = true, Enabled = false };
    private readonly Button log = new() { Text = "查看导出日志", AutoSize = true, Enabled = false };
    private readonly Label summary = new() { Dock = DockStyle.Top, Height = 72, Padding = new Padding(8), Text = "按真实资源路径收集，不受搜索/类型筛选影响。~ 推测路径不参与。" };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(8), AutoEllipsis = true };
    private readonly ListView preview = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, VirtualMode = true };
    private DirectoryExportPlan plan;
    private CancellationTokenSource operation;
    private string lastOutput, lastReport;
    public string ResourceDirectory => directory.Text;
    public string ExportDirectory { get; private set; }

    public DirectoryExportForm(AssetIndexStore store, IStudioBridge bridge, CatalogRequest context, string initialDirectory, string exportDirectory)
    {
        this.store = store; this.bridge = bridge; this.context = context; ExportDirectory = exportDirectory;
        Text = "按目录收集与导出主资源"; Width = 1200; Height = 720; MinimumSize = new Size(900, 520);
        StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        directory.Text = initialDirectory; workers.Value = Math.Clamp(context.ExportWorkers, 1, 8);
        var pathBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        pathBar.Controls.AddRange([directory, recursive, collect]);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        actions.Controls.AddRange([export, cancel, open, log, new Label { Text = "并发数", AutoSize = true, Padding = new Padding(8) }, workers, new Label { Text = "默认 2；内存紧张或机械硬盘可设为 1", AutoSize = true, Padding = new Padding(8) }]);
        preview.Columns.Add("主资源路径", 740); preview.Columns.Add("处理方式", 175); preview.Columns.Add("资源对象数", 100);
        preview.RetrieveVirtualItem += (_, e) =>
        {
            if (plan == null || e.ItemIndex >= plan.Groups.Length) { e.Item = new ListViewItem(""); return; }
            var group = plan.Groups[e.ItemIndex];
            e.Item = new ListViewItem([group.ResourcePath, group.IsFbx ? "FBX（子动画整合）" : "主资源/复合资源", group.Members.Length.ToString()]);
        };
        Controls.Add(preview); Controls.Add(status); Controls.Add(summary); Controls.Add(actions); Controls.Add(pathBar);
        collect.Click += async (_, _) => await CollectAsync();
        export.Click += async (_, _) => await ExportAsync();
        cancel.Click += (_, _) => operation?.Cancel();
        open.Click += (_, _) => { if (Directory.Exists(lastOutput)) Process.Start(new ProcessStartInfo(lastOutput) { UseShellExecute = true }); };
        log.Click += (_, _) => { if (File.Exists(lastReport)) Process.Start(new ProcessStartInfo(lastReport) { UseShellExecute = true }); };
        directory.TextChanged += (_, _) => InvalidatePlan(); recursive.CheckedChanged += (_, _) => InvalidatePlan();
        FormClosing += (_, e) => { if (operation != null) { operation.Cancel(); e.Cancel = true; Status("正在取消；任务结束后可关闭窗口。"); } };
    }

    private void InvalidatePlan() { preview.VirtualListSize = 0; plan = null; summary.Text = "目录条件已变化，请重新收集；导出时也会自动收集。"; }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation != null) return;
        using var cts = new CancellationTokenSource(); operation = cts;
        workers.Enabled = directory.Enabled = recursive.Enabled = collect.Enabled = export.Enabled = false; cancel.Enabled = true;
        try { await action(cts.Token); }
        catch (OperationCanceledException) { Status("任务已取消。"); }
        catch (Exception ex) { Status("失败：" + ex.Message); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { operation = null; workers.Enabled = directory.Enabled = recursive.Enabled = collect.Enabled = export.Enabled = true; cancel.Enabled = false; }
    }

    private Task CollectAsync()
    {
        var path = directory.Text; var children = recursive.Checked;
        return RunAsync(async token =>
        {
            Status("正在收集目录资源…");
            var collected = await Task.Run(() => store.CollectDirectory(path, children, token), token);
            preview.VirtualListSize = 0; plan = collected; preview.VirtualListSize = plan.Groups.Length; preview.Invalidate();
            summary.Text = $"{plan.Directory} · {plan.Groups.Length:N0} 个主资源，{plan.MatchedRows - plan.DuplicateRows:N0} 个对象，去重 {plan.DuplicateRows:N0} 条。" +
                "\n每个 FBX 只包含归属于自己的子动画；文件直接保留相对目录，日志单独保存。";
            Status(plan.Groups.Length == 0 ? "没有匹配项。请确认资源路径已还原、目录完整且清单是最新的。" : "收集完成，可以一键导出。失败项会写入报告并继续处理其他主资源。");
        });
    }

    private async Task ExportAsync()
    {
        if (plan == null) await CollectAsync();
        if (plan == null || plan.Groups.Length == 0 || operation != null) return;
        using var dialog = new FolderBrowserDialog { Description = "选择目录导出的保存位置", UseDescriptionForTitle = true, SelectedPath = ExportDirectory };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ExportDirectory = dialog.SelectedPath;
        var snapshot = plan;
        await RunAsync(async token =>
        {
            var result = await bridge.ExportDirectoryAsync(snapshot, context with { ExportWorkers = ExportWorkers }, ExportDirectory, Status, token);
            lastOutput = result.OutputDirectory; lastReport = result.ReportPath; open.Enabled = Directory.Exists(lastOutput); log.Enabled = File.Exists(lastReport);
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
