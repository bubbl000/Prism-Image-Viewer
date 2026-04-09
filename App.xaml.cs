using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;

namespace ImageBrowser;

public partial class App : Application
{
    private const string GlobalMutexName = "Local\\ImageBrowser_SingleInstance";
    private const string PipeName        = "ImageBrowser_IPC";

    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThumbnailCache.CheckAndClean(expireMinutes: 30);
        LocalizationManager.Apply(AppSettings.Current.Language);

        string? filePath = e.Args.Length > 0 && File.Exists(e.Args[0])
            ? Path.GetFullPath(e.Args[0])
            : null;

        if (!AppSettings.Current.MultiWindow)
        {
            // ── 单窗口模式：全局单实例 ──────────────────────────────
            _mutex = new Mutex(true, GlobalMutexName, out bool isFirst);
            if (!isFirst)
            {
                // 已有实例运行：把文件路径发过去，本进程退出
                TrySendToExistingInstance(filePath ?? "");
                Shutdown();
                return;
            }
            StartPipeServer();
        }

        // 启动主窗口
        var win = new MainWindow();
        win.Show();
        if (filePath != null) win.LoadFromPath(filePath);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThumbnailCache.TouchMarker();
        _mutex?.ReleaseMutex();
        base.OnExit(e);
    }

    // ── 发送文件路径给已有实例 ────────────────────────────────────
    private static void TrySendToExistingInstance(string filePath)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(1500);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(filePath);
        }
        catch { /* 连接失败时静默忽略 */ }
    }

    // ── 管道服务端（在后台线程持续监听） ─────────────────────────
    private void StartPipeServer()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(
                        PipeName, PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None);

                    pipe.WaitForConnection();

                    using var reader = new StreamReader(pipe);
                    string? msg = reader.ReadLine();

                    if (msg != null)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            var mainWin = Windows.OfType<MainWindow>().FirstOrDefault();
                            if (mainWin == null) return;

                            mainWin.Activate();
                            if (mainWin.WindowState == WindowState.Minimized)
                                mainWin.WindowState = WindowState.Normal;

                            if (!string.IsNullOrEmpty(msg) && File.Exists(msg))
                                mainWin.LoadFromPath(msg);
                        });
                    }
                }
                catch { break; }
            }
        })
        {
            IsBackground = true,
            Name = "IPC-PipeServer"
        };
        thread.Start();
    }
}

