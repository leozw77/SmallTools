using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ProbeContext());
    }
}

internal sealed class ProbeContext : ApplicationContext
{
    private const int HotkeyId = 1;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint Vk9 = 0x39;
    private const int WmHotkey = 0x0312;

    private readonly ProbeWindow window;
    private readonly string logPath;

    public ProbeContext()
    {
        logPath = Path.Combine(Application.StartupPath, "uia-probe.log");
        File.WriteAllText(logPath,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
            " START UIAReadProbe hotkey=Ctrl+Alt+9\r\n",
            new UTF8Encoding(true));

        window = new ProbeWindow(logPath, HotkeyId);
        if (!RegisterHotKey(window.Handle, HotkeyId, ModControl | ModAlt, Vk9))
            Append("HOTKEY_REGISTER_FAILED win32=" + Marshal.GetLastWin32Error());
        else
            Append("READY press Ctrl+Alt+9 while target input keeps focus");
    }

    private void Append(string message)
    {
        try
        {
            File.AppendAllText(logPath,
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
    }

    protected override void ExitThreadCore()
    {
        try { UnregisterHotKey(window.Handle, HotkeyId); } catch { }
        window.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private sealed class ProbeWindow : NativeWindow, IDisposable
    {
        private readonly string logPath;
        private readonly int hotkeyId;

        public ProbeWindow(string path, int id)
        {
            logPath = path;
            hotkeyId = id;
            CreateHandle(new CreateParams { Caption = "UIAReadProbeHiddenWindow" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == hotkeyId)
                ReadFocusedControl();
            base.WndProc(ref m);
        }

        private void ReadFocusedControl()
        {
            try
            {
                AutomationElement element = AutomationElement.FocusedElement;
                if (element == null)
                {
                    Log("READ no_focused_element");
                    return;
                }

                string name = Safe(() => element.Current.Name);
                string className = Safe(() => element.Current.ClassName);
                string controlType = Safe(() => element.Current.ControlType.ProgrammaticName);
                int pid = 0;
                bool isPassword = false;
                try { pid = element.Current.ProcessId; } catch { }
                try { isPassword = element.Current.IsPassword; } catch { }

                string processName = "";
                try { if (pid > 0) processName = Process.GetProcessById(pid).ProcessName; } catch { }

                Log("FOCUSED process=" + Escape(processName) +
                    " pid=" + pid +
                    " type=" + Escape(controlType) +
                    " class=" + Escape(className) +
                    " name=" + Escape(name) +
                    " isPassword=" + (isPassword ? "1" : "0"));

                if (isPassword)
                {
                    Log("PASSWORD_SKIPPED");
                    return;
                }

                object pattern;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                {
                    string value = ((ValuePattern)pattern).Current.Value ?? "";
                    Log("VALUE len=" + value.Length + " text=\"" + Escape(value) + "\"");
                    if (value.Length > 0) return;
                }

                if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    string text = ((TextPattern)pattern).DocumentRange.GetText(-1) ?? "";
                    Log("TEXT len=" + text.Length + " text=\"" + Escape(text) + "\"");
                    return;
                }

                Log("NO_VALUE_OR_TEXT_PATTERN");
            }
            catch (Exception ex)
            {
                Log("READ_ERROR " + ex.GetType().Name + " " + Escape(ex.Message));
            }
        }

        private string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }

        private string Escape(string text)
        {
            if (text == null) return "";
            return text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"");
        }

        private void Log(string message)
        {
            try
            {
                File.AppendAllText(logPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        public void Dispose()
        {
            try { DestroyHandle(); } catch { }
        }
    }
}
