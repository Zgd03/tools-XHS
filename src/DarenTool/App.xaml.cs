using System.IO;
using System.Windows;

namespace DarenTool;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(true, "Local\\DarenToolV2", out var first);
        if (!first) { MessageBox.Show("工具已经打开，请使用现有窗口。", "达人 Excel 工具"); Shutdown(); return; }
        Directory.CreateDirectory(AppPaths.Root);
        DispatcherUnhandledException += (_, args) =>
        {
            var log = Path.Combine(AppPaths.Root, "启动错误.log");
            try { File.AppendAllText(log, DateTimeOffset.Now + " " + args.Exception + Environment.NewLine); } catch { }
            MessageBox.Show("操作未完成：" + args.Exception.Message + "\n详细信息：" + log, "达人 Excel 工具");
            args.Handled = true;
        };
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}

public static class AppPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DarenToolV2");
    public static string Results => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "达人处理结果");
}
