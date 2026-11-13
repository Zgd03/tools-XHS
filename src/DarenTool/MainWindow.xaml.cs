using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using DarenTool.Browser;
using DarenTool.Core;

namespace DarenTool;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<RowResult> rows = [];
    private BrowserSession? xhs;
    private BrowserSession? pgy;
    private CancellationTokenSource? cancellation;
    private string? resultsDirectory;
    private bool busy;
    private readonly string settingsFile = Path.Combine(AppPaths.Root, "settings.json");
    private ProcessMode Mode => (ProcessMode)Math.Max(0, ModeBox.SelectedIndex);
    public MainWindow()
    {
        InitializeComponent();
        RowsGrid.ItemsSource = rows;
        OutputBox.Text = AppPaths.Results;
        try
        {
            if (File.Exists(settingsFile) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsFile)) is { } settings)
            { SourceBox.Text = settings.Source; OutputBox.Text = settings.Output; ModeBox.SelectedIndex = Math.Clamp(settings.Mode, 0, 3); }
        }
        catch { /* An invalid preference file must not prevent launching. */ }
        var argument = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(x => x.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase));
        if (argument is not null) SourceBox.Text = argument;
        RefreshHint();
    }

    private BrowserSession Xhs => xhs ??= new BrowserSession(false, this);
    private BrowserSession Pgy
    {
        get
        {
            if (pgy is null)
            {
                pgy = new BrowserSession(true, this);
                pgy.BrandChanged += summary => BrandText.Text = "蒲公英：" + summary;
            }
            return pgy;
        }
    }
    private void ChooseSource(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择达人数据工作簿", Filter = "Excel 工作簿 (*.xlsx)|*.xlsx", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) { SourceBox.Text = dialog.FileName; StatusText.Text = "文件已选择"; }
    }
    private void ChooseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择结果保存目录", InitialDirectory = Directory.Exists(OutputBox.Text) ? OutputBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FolderName;
    }
    private void FileDrop(object sender, DragEventArgs e)
    {
        if (busy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1 && files[0].EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        { SourceBox.Text = files[0]; StatusText.Text = "文件已选择"; }
        else MessageBox.Show(this, "请拖入一个 .xlsx 工作簿。", "选择文件");
    }
    private void ModeChanged(object sender, SelectionChangedEventArgs e) { if (ModeHint is not null) RefreshHint(); }
    private void RefreshHint() => ModeHint.Text = Mode switch
    {
        ProcessMode.Five => "曝光、阅读来自当前蒲公英品牌；点赞、收藏、评论来自小红书。请先完成两处登录。",
        ProcessMode.Interaction => "仅采集点赞、收藏、评论，无需接入蒲公英。",
        ProcessMode.Shorten => "生成官方短链并核验笔记 ID；已有短链跳过，长链接访问参数保留。",
        _ => "先转换并核验链接，再更新五项数据；部分转链失败时保留原链接继续处理。"
    };
    private async void LoginXhs(object sender, RoutedEventArgs e) => await ShowBrowserAsync(false);
    private async void LoginPgy(object sender, RoutedEventArgs e) => await ShowBrowserAsync(true);
    private async Task ShowBrowserAsync(bool isPgy)
    {
        try { await (isPgy ? Pgy : Xhs).ShowAsync(); }
        catch (Exception ex) { ShowFailure(ex); }
    }
    private async void DetectBrand(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true); cancellation = new();
        try
        {
            StatusText.Text = "正在读取品牌与报表…";
            await Pgy.DetectBrandAsync(cancellation.Token);
            BrandText.Text = "蒲公英：" + Pgy.BrandSummary;
            StatusText.Text = "品牌已确认，开始处理时获取完整报表";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消品牌读取"; }
        catch (Exception ex) { StatusText.Text = "品牌尚未读取"; ShowFailure(ex); }
        finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
    }
    private async void StartRun(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var source = SourceBox.Text.Trim().Trim('"');
        var output = OutputBox.Text.Trim().Trim('"');
        if (source.Length == 0 || output.Length == 0) { MessageBox.Show(this, "请选择 Excel 文件和结果保存目录。", "补全路径"); return; }
        try { source = Path.GetFullPath(source); output = Path.GetFullPath(output); }
        catch (Exception ex) { ShowFailure(ex); return; }
        if (!File.Exists(source)) { MessageBox.Show(this, "源文件不存在，请重新选择。", "选择文件"); return; }
        rows.Clear(); LogBox.Clear(); resultsDirectory = null; ResultsButton.IsEnabled = false;
        SetBusy(true); cancellation = new();
        var service = new ProcessingService(Xhs, Pgy, AppendLog, ShowRow, (text, done, total) =>
        {
            StatusText.Text = total > 0 ? $"{text} · {done}/{total}" : text;
            Progress.IsIndeterminate = done == 0;
            Progress.Maximum = Math.Max(1, total); Progress.Value = done;
        });
        try
        {
            File.WriteAllText(settingsFile, JsonSerializer.Serialize(new Settings(source, output, (int)Mode)));
            StatusText.Text = "正在读取工作簿…";
            var outcome = await service.RunAsync(source, output, Mode, ResumeCheck.IsChecked == true, cancellation.Token);
            resultsDirectory = outcome.Directory;
            StatusText.Text = outcome.Cancelled ? "已取消，已完成的结果已保存" : $"处理结束 · 完整 {outcome.Complete}，部分 {outcome.Partial}，失败 {outcome.Failed}";
            if (!outcome.HasWorkbook) AppendLog("未取得可写入的修改，没有生成重复工作簿。请查看明细和日志。");
            ResultsButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消"; }
        catch (Exception ex)
        {
            resultsDirectory = service.OutputDirectory;
            ResultsButton.IsEnabled = resultsDirectory is not null;
            StatusText.Text = "处理未完成，请查看日志和明细";
            AppendLog("处理未完成：" + ex.Message); ShowFailure(ex);
        }
        finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
    }
    private void SetBusy(bool value)
    {
        busy = value; Inputs.IsEnabled = !value; StartButton.IsEnabled = !value; ResumeCheck.IsEnabled = !value;
        // Existing browser windows remain accessible for user login/verification during collection.
        foreach (var child in LoginControls.Children.OfType<Button>().Where(b => b.Content?.ToString() == "读取当前品牌")) child.IsEnabled = !value;
        CancelButton.IsEnabled = value; Progress.IsIndeterminate = value;
        if (!value) Progress.IsIndeterminate = false;
    }
    private void CancelRun(object sender, RoutedEventArgs e) { cancellation?.Cancel(); CancelButton.IsEnabled = false; StatusText.Text = "正在取消并保存已完成结果…"; }
    private void ShowRow(RowResult row)
    {
        var old = rows.FirstOrDefault(r => r.Row == row.Row);
        if (old is not null) rows[rows.IndexOf(old)] = row; else rows.Add(row);
        RowsGrid.ScrollIntoView(row);
    }
    private void AppendLog(string text) { LogBox.AppendText(text + Environment.NewLine); LogBox.ScrollToEnd(); }
    private async void ClearBrowserCache(object sender, RoutedEventArgs e)
    {
        if (busy) { MessageBox.Show(this, "请等待当前处理结束后再清理缓存。", "正在处理"); return; }
        SetBusy(true);
        try
        {
            if (xhs is not null) await xhs.ClearCacheAsync();
            if (pgy is not null) await pgy.ClearCacheAsync();
            AppendLog("已清理当前浏览器会话的网页磁盘缓存，登录资料保留。");
            StatusText.Text = "网页缓存已清理";
        }
        catch (Exception ex) { ShowFailure(ex); }
        finally { SetBusy(false); }
    }
    private void OpenResults(object sender, RoutedEventArgs e)
    {
        if (resultsDirectory is not null && Directory.Exists(resultsDirectory))
            try { Process.Start(new ProcessStartInfo(resultsDirectory) { UseShellExecute = true }); } catch (Exception ex) { ShowFailure(ex); }
    }
    private void ShowFailure(Exception ex)
    {
        var message = ex is WebView2RuntimeNotFoundException
            ? "本机缺少 Microsoft Edge WebView2 Runtime。请安装微软的 WebView2 Evergreen Runtime 后重新启动工具。\n下载地址见开发目录中的“开始使用.txt”。"
            : ex.Message;
        MessageBox.Show(this, message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; MessageBox.Show(this, "请先取消处理，等待结果保存完成后关闭。", "正在处理"); return; }
        xhs?.Dispose(); pgy?.Dispose();
    }
    private record Settings(string Source, string Output, int Mode);
}
