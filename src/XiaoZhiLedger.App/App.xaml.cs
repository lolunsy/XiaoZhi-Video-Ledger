using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using XiaoZhiLedger.Core.Storage;

namespace XiaoZhiLedger.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            WriteCrashLog(eventArgs.ExceptionObject as Exception ?? new Exception(eventArgs.ExceptionObject.ToString()));
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            WriteCrashLog(eventArgs.Exception);
            eventArgs.SetObserved();
        };
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        var logPath = WriteCrashLog(eventArgs.Exception);
        MessageBox.Show(
            $"小智剪辑分类账遇到异常，账本和原视频没有被删除。\n\n" +
            $"详情已写入：{logPath}\n\n{eventArgs.Exception.Message}",
            "运行异常",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        eventArgs.Handled = true;
    }

    private static string WriteCrashLog(Exception error)
    {
        try
        {
            var directory = Path.Combine(StoreLocationResolver.ResolveDataDirectory(), "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            File.WriteAllText(path,
                $"小智剪辑分类账 v0.1.0 正式版\n时间：{DateTimeOffset.Now:o}\n版本：{typeof(App).Assembly.GetName().Version}\n\n{error}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch
        {
            return "日志写入失败";
        }
    }
}
