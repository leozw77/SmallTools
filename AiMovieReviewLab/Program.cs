namespace AiMovieReviewLab;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatalStartupOrUiException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportFatalStartupOrUiException(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown unhandled exception"));

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            ReportFatalStartupOrUiException(ex);
        }
    }

    private static void ReportFatalStartupOrUiException(Exception ex)
    {
        string? logPath = null;
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiMovieReviewLab");
            Directory.CreateDirectory(root);
            logPath = Path.Combine(root, "startup-crash.log");
            File.WriteAllText(logPath,
                $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}" +
                $"Version: {Application.ProductVersion}{Environment.NewLine}" +
                $"OS: {Environment.OSVersion}{Environment.NewLine}" +
                $"DPI: {GetDpiSafe()}{Environment.NewLine}" +
                $"Exception:{Environment.NewLine}{ex}");
        }
        catch
        {
            // Crash reporting must never hide the original exception.
        }

        var message = "程序发生未处理异常。" + Environment.NewLine + Environment.NewLine + ex.Message;
        if (!string.IsNullOrWhiteSpace(logPath))
            message += Environment.NewLine + Environment.NewLine + "诊断日志：" + logPath;

        try
        {
            MessageBox.Show(message, "AI 观影短评实验台 - 启动/运行异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Nothing else can be shown safely at this point.
        }
    }

    private static int GetDpiSafe()
    {
        try
        {
            using var graphics = Graphics.FromHwnd(IntPtr.Zero);
            return (int)Math.Round(graphics.DpiX);
        }
        catch
        {
            return 0;
        }
    }
}
