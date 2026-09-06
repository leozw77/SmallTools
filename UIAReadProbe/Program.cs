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

    private readonly ProbeWindow window;
    private readonly string logPath;

    public ProbeContext()
    {
        logPath = Path.Combine(
            Application.StartupPath,
            "uia-probe.log");

        try
        {
            File.WriteAllText(
                logPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                + " START UIAReadProbe v0.3 diagnostic hotkey=Ctrl+Alt+9\r\n",
                new UTF8Encoding(true));
        }
        catch
        {
        }

        window = new ProbeWindow(logPath, HotkeyId);

        if (!RegisterHotKey(
            window.Handle,
            HotkeyId,
            ModControl | ModAlt,
            Vk9))
        {
            Append(
                "HOTKEY_REGISTER_FAILED win32="
                + Marshal.GetLastWin32Error());
        }
        else
        {
            Append(
                "READY press Ctrl+Alt+9 while target input keeps focus");
        }
    }

    private void Append(string message)
    {
        try
        {
            File.AppendAllText(
                logPath,
                DateTime.Now.ToString("HH:mm:ss.fff")
                + " "
                + message
                + Environment.NewLine,
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    protected override void ExitThreadCore()
    {
        try
        {
            UnregisterHotKey(window.Handle, HotkeyId);
        }
        catch
        {
        }

        window.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(
        IntPtr hWnd,
        int id);

    private sealed class ProbeWindow : NativeWindow, IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const int MaxLoggedTextLength = 200;

        private readonly string logPath;
        private readonly int hotkeyId;

        public ProbeWindow(string path, int id)
        {
            logPath = path;
            hotkeyId = id;

            CreateHandle(
                new CreateParams
                {
                    Caption = "UIAReadProbeHiddenWindow"
                });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey &&
                m.WParam.ToInt32() == hotkeyId)
            {
                ReadFocusedControl();
            }

            base.WndProc(ref m);
        }

        private void ReadFocusedControl()
        {
            try
            {
                AutomationElement element =
                    AutomationElement.FocusedElement;

                if (element == null)
                {
                    Log("READ no_focused_element");
                    return;
                }

                string name = Safe(delegate
                {
                    return element.Current.Name;
                });

                string className = Safe(delegate
                {
                    return element.Current.ClassName;
                });

                string controlType = Safe(delegate
                {
                    return element.Current.ControlType.ProgrammaticName;
                });

                int pid = 0;
                bool isPassword = false;

                try
                {
                    pid = element.Current.ProcessId;
                }
                catch
                {
                }

                try
                {
                    isPassword = element.Current.IsPassword;
                }
                catch
                {
                }

                string processName = "";

                try
                {
                    if (pid > 0)
                    {
                        processName =
                            Process
                            .GetProcessById(pid)
                            .ProcessName;
                    }
                }
                catch
                {
                }

                string windowTitle = GetForegroundWindowTitle();

                Log(
                    "FOCUSED"
                    + " process=" + EscapeAndTruncate(processName)
                    + " pid=" + pid
                    + " window=\"" + EscapeAndTruncate(windowTitle) + "\""
                    + " type=" + EscapeAndTruncate(controlType)
                    + " class=" + EscapeAndTruncate(className)
                    + " name=\"" + EscapeAndTruncate(name) + "\""
                    + " isPassword="
                    + (isPassword ? "1" : "0"));

                if (isPassword)
                {
                    Log("PASSWORD_SKIPPED");
                    return;
                }

                Log("ROOT_BEGIN");
                AppendNodeDetails("ROOT", element);

                try
                {
                    AutomationElementCollection children =
                        element.FindAll(
                            TreeScope.Children,
                            Condition.TrueCondition);

                    Log(
                        "DIRECT_CHILDREN count="
                        + children.Count);

                    for (int i = 0; i < children.Count; i++)
                    {
                        AppendNodeDetails(
                            "CHILD[" + i + "]",
                            children[i]);
                    }
                }
                catch (Exception ex)
                {
                    Log(
                        "DIRECT_CHILDREN_ERROR "
                        + ex.GetType().Name
                        + " "
                        + EscapeAndTruncate(ex.Message));
                }

                Log(
                    "DIAGNOSTIC_SUMMARY root_and_direct_children_only");
                Log("NO_TEXT_SELECTION_OR_SEND");
                Log("READ_DONE");
            }
            catch (Exception ex)
            {
                Log(
                    "READ_ERROR "
                    + ex.GetType().Name
                    + " "
                    + EscapeAndTruncate(ex.Message));
            }
        }

        private void AppendNodeDetails(
            string label,
            AutomationElement element)
        {
            try
            {
                if (element == null)
                {
                    Log(label + " ERROR=null_element");
                    return;
                }

                string controlType = "";

                try
                {
                    ControlType type = element.Current.ControlType;
                    controlType =
                        type.ProgrammaticName
                        + " id="
                        + type.Id;
                }
                catch (Exception ex)
                {
                    controlType =
                        "error="
                        + ex.GetType().Name;
                }

                string className = Safe(delegate
                {
                    return element.Current.ClassName;
                });

                string name = Safe(delegate
                {
                    return element.Current.Name;
                });

                Log(
                    label
                    + " ControlType="
                    + EscapeAndTruncate(controlType));
                Log(
                    label
                    + " ClassName=\""
                    + EscapeAndTruncate(className)
                    + "\"");
                Log(
                    label
                    + " Name=\""
                    + EscapeAndTruncate(name)
                    + "\"");

                LogValuePattern(label, element);
                LogTextPattern(label, element);
                LogLegacyIAccessible(label, element);
            }
            catch (Exception ex)
            {
                Log(
                    label
                    + " ERROR="
                    + ex.GetType().Name
                    + " "
                    + EscapeAndTruncate(ex.Message));
            }
        }

        private void LogValuePattern(
            string label,
            AutomationElement element)
        {
            try
            {
                object pattern;

                if (!element.TryGetCurrentPattern(
                    ValuePattern.Pattern,
                    out pattern))
                {
                    Log(label + " ValuePattern=(unsupported)");
                    return;
                }

                string value =
                    ((ValuePattern)pattern).Current.Value
                    ?? "";

                Log(
                    label
                    + " ValuePattern "
                    + FormatText(value));
            }
            catch (Exception ex)
            {
                Log(
                    label
                    + " ValuePattern_ERROR "
                    + ex.GetType().Name
                    + " "
                    + EscapeAndTruncate(ex.Message));
            }
        }

        private void LogTextPattern(
            string label,
            AutomationElement element)
        {
            try
            {
                object pattern;

                if (!element.TryGetCurrentPattern(
                    TextPattern.Pattern,
                    out pattern))
                {
                    Log(label + " TextPattern=(unsupported)");
                    return;
                }

                TextPattern textPattern =
                    (TextPattern)pattern;

                string text =
                    textPattern.DocumentRange.GetText(-1)
                    ?? "";

                Log(
                    label
                    + " TextPattern "
                    + FormatText(text));
            }
            catch (Exception ex)
            {
                Log(
                    label
                    + " TextPattern_ERROR "
                    + ex.GetType().Name
                    + " "
                    + EscapeAndTruncate(ex.Message));
            }
        }

        private void LogLegacyIAccessible(
            string label,
            AutomationElement element)
        {
            try
            {
                object pattern;

                if (!element.TryGetCurrentPattern(
                    LegacyIAccessiblePattern.Pattern,
                    out pattern))
                {
                    Log(
                        label
                        + " LegacyIAccessible=(unsupported)");
                    return;
                }

                string value =
                    ((LegacyIAccessiblePattern)pattern)
                    .Current
                    .Value
                    ?? "";

                Log(
                    label
                    + " LegacyIAccessible "
                    + FormatText(value));
            }
            catch (Exception ex)
            {
                Log(
                    label
                    + " LegacyIAccessible_ERROR "
                    + ex.GetType().Name
                    + " "
                    + EscapeAndTruncate(ex.Message));
            }
        }

        private string FormatText(string text)
        {
            if (String.IsNullOrEmpty(text))
                return "len=0 text=\"\"";

            return
                "len="
                + text.Length
                + " text=\""
                + EscapeAndTruncate(text)
                + "\"";
        }

        private string GetForegroundWindowTitle()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();

                if (hwnd == IntPtr.Zero)
                    return "";

                StringBuilder sb =
                    new StringBuilder(2048);

                GetWindowText(
                    hwnd,
                    sb,
                    sb.Capacity);

                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        private string Safe(Func<string> getter)
        {
            try
            {
                return getter() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private string EscapeAndTruncate(string text)
        {
            if (text == null)
                return "(null)";

            string escaped = text
                .Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\"", "\\\"");

            if (escaped.Length > MaxLoggedTextLength)
            {
                return escaped.Substring(
                    0,
                    MaxLoggedTextLength)
                    + "...";
            }

            return escaped;
        }

        private void Log(string message)
        {
            try
            {
                File.AppendAllText(
                    logPath,
                    DateTime.Now.ToString("HH:mm:ss.fff")
                    + " "
                    + message
                    + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            try
            {
                DestroyHandle();
            }
            catch
            {
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport(
            "user32.dll",
            CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(
            IntPtr hWnd,
            StringBuilder lpString,
            int nMaxCount);
    }
}
