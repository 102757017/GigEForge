namespace GigEVision.App;

/// <summary>
/// 应用入口：初始化 WinForms（PerMonitorV2 高 DPI 由 csproj 的 ApplicationHighDpiMode 声明），
/// 并挂接全局异常捕获——任何未处理异常以中文 MessageBox 呈现，而不是静默崩溃。
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // UI 线程未处理异常（按钮点击、事件处理器等）
        Application.ThreadException += (_, e) =>
            ShowFatal("界面线程异常", e.Exception);

        // 非界面线程异常兜底（后台线程抛出的异常最终会进入 AppDomain 级处理）
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ShowFatal("未处理异常", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    /// <summary>弹窗展示致命异常详情（中文提示）。</summary>
    private static void ShowFatal(string title, Exception? ex)
    {
        string detail = ex is null
            ? "未知错误（异常对象为空）。"
            : $"{ex.GetType().FullName}: {ex.Message}\n\n{ex.StackTrace}";

        try
        {
            MessageBox.Show(
                detail,
                $"GigEForge — {title}",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // 消息循环已失效时弹窗本身可能失败，此时只能放弃 UI 反馈
        }
    }
}
