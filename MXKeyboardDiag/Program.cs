using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new DiagForm());
    }
}

internal sealed class DiagForm : Form
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_INPUT = 0x00FF;

    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIM_TYPEKEYBOARD = 1;

    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_0 = 0x30;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_NONAME = 0xFC;

    private readonly TextBox logBox;
    private readonly Label statusLabel;
    private readonly Button copyButton;
    private readonly Button clearButton;
    private readonly Button openFolderButton;
    private readonly Timer stateTimer;

    private readonly LowLevelKeyboardProc keyboardProc;
    private IntPtr keyboardHook = IntPtr.Zero;
    private StreamWriter writer;
    private string logPath;
    private long sequence;

    private readonly Dictionary<int, bool> sampledStates = new Dictionary<int, bool>();
    private readonly Dictionary<IntPtr, string> deviceNames = new Dictionary<IntPtr, string>();

    public DiagForm()
    {
        Text = "MX Keyboard / Raw Input Diagnostic v0.1";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(950, 560);
        Size = new Size(1180, 720);
        Font = new Font("Microsoft YaHei UI", 9F);

        var topPanel = new Panel();
        topPanel.Dock = DockStyle.Top;
        topPanel.Height = 70;

        statusLabel = new Label();
        statusLabel.Left = 12;
        statusLabel.Top = 10;
        statusLabel.Width = 900;
        statusLabel.Height = 44;
        statusLabel.Text = "只监听，不拦截任何按键。建议先退出 MXBackspaceHold，再测试 MX 侧键按住 2 秒后松开。";

        copyButton = new Button();
        copyButton.Text = "复制全部";
        copyButton.Width = 90;
        copyButton.Height = 28;
        copyButton.Top = 9;
        copyButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        copyButton.Left = ClientSize.Width - 300;
        copyButton.Click += delegate
        {
            try { Clipboard.SetText(logBox.Text); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "复制失败"); }
        };

        clearButton = new Button();
        clearButton.Text = "清空显示";
        clearButton.Width = 90;
        clearButton.Height = 28;
        clearButton.Top = 9;
        clearButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        clearButton.Left = ClientSize.Width - 200;
        clearButton.Click += delegate
        {
            logBox.Clear();
            WriteLog("MARK", "===== display cleared =====");
        };

        openFolderButton = new Button();
        openFolderButton.Text = "打开日志目录";
        openFolderButton.Width = 100;
        openFolderButton.Height = 28;
        openFolderButton.Top = 9;
        openFolderButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        openFolderButton.Left = ClientSize.Width - 100;
        openFolderButton.Click += delegate
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + logPath + "\"");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "打开失败"); }
        };

        topPanel.Controls.Add(statusLabel);
        topPanel.Controls.Add(copyButton);
        topPanel.Controls.Add(clearButton);
        topPanel.Controls.Add(openFolderButton);

        logBox = new TextBox();
        logBox.Dock = DockStyle.Fill;
        logBox.Multiline = true;
        logBox.ScrollBars = ScrollBars.Both;
        logBox.WordWrap = false;
        logBox.ReadOnly = true;
        logBox.Font = new Font("Consolas", 9F);

        Controls.Add(logBox);
        Controls.Add(topPanel);

        keyboardProc = KeyboardHookCallback;

        stateTimer = new Timer();
        stateTimer.Interval = 10;
        stateTimer.Tick += SampleImportantKeyStates;

        Shown += delegate
        {
            InitializeLog();
            RegisterRawInput();
            InstallKeyboardHook();
            InitializeSampledStates();
            stateTimer.Start();

            WriteLog("INFO", "Diagnostic started. No input is blocked or modified.");
            WriteLog("INFO", "Recommended test: close MXBackspaceHold -> focus WeChat input -> hold MX voice button 2s -> release -> return here.");
            WriteLog("INFO", "Log file: " + logPath);
        };
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        stateTimer.Stop();
        if (keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(keyboardHook);
            keyboardHook = IntPtr.Zero;
        }

        if (writer != null)
        {
            try { writer.Flush(); writer.Dispose(); }
            catch { }
            writer = null;
        }

        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
        {
            try { HandleRawInput(m.LParam); }
            catch (Exception ex) { WriteLog("RAW-ERR", ex.GetType().Name + ": " + ex.Message); }
        }

        base.WndProc(ref m);
    }

    private void InitializeLog()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            folder = Path.GetTempPath();

        logPath = Path.Combine(folder, "MXKeyboardDiag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        writer = new StreamWriter(logPath, true, new UTF8Encoding(true));
        writer.AutoFlush = true;
        statusLabel.Text = "只监听，不拦截。日志自动保存到：" + logPath;
    }

    private void RegisterRawInput()
    {
        RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[2];
        devices[0].usUsagePage = 0x01;
        devices[0].usUsage = 0x06; // keyboard
        devices[0].dwFlags = RIDEV_INPUTSINK;
        devices[0].hwndTarget = Handle;

        devices[1].usUsagePage = 0x01;
        devices[1].usUsage = 0x02; // mouse
        devices[1].dwFlags = RIDEV_INPUTSINK;
        devices[1].hwndTarget = Handle;

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
            WriteLog("WARN", "RegisterRawInputDevices failed, Win32=" + Marshal.GetLastWin32Error());
        else
            WriteLog("INFO", "Raw Input registered for keyboard + mouse (INPUTSINK).");
    }

    private void InstallKeyboardHook()
    {
        keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, GetModuleHandle(null), 0);
        if (keyboardHook == IntPtr.Zero)
            WriteLog("WARN", "WH_KEYBOARD_LL install failed, Win32=" + Marshal.GetLastWin32Error());
        else
            WriteLog("INFO", "WH_KEYBOARD_LL installed.");
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            string msg = KeyboardMessageName(wParam.ToInt32());
            bool injected = (data.flags & 0x10) != 0;
            bool lowerIl = (data.flags & 0x02) != 0;
            bool extended = (data.flags & 0x01) != 0;
            bool altDownFlag = (data.flags & 0x20) != 0;
            bool upFlag = (data.flags & 0x80) != 0;

            string detail = string.Format(
                "{0} vk=0x{1:X2}({1}) key={2} scan=0x{3:X4} flags=0x{4:X2} ext={5} injected={6} lowerIL={7} altFlag={8} upFlag={9} extra=0x{10:X} fg=\"{11}\"",
                msg,
                data.vkCode,
                KeyName(data.vkCode),
                data.scanCode,
                data.flags,
                extended ? 1 : 0,
                injected ? 1 : 0,
                lowerIl ? 1 : 0,
                altDownFlag ? 1 : 0,
                upFlag ? 1 : 0,
                data.dwExtraInfo.ToUInt64(),
                ForegroundTitle());

            WriteLog("LLK", detail);
        }

        return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint read = GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize);
            if (read == 0 || read == 0xFFFFFFFF) return;

            RAWINPUTHEADER header = (RAWINPUTHEADER)Marshal.PtrToStructure(buffer, typeof(RAWINPUTHEADER));
            IntPtr payload = IntPtr.Add(buffer, (int)headerSize);
            string device = DeviceName(header.hDevice);

            if (header.dwType == RIM_TYPEKEYBOARD)
            {
                RAWKEYBOARD k = (RAWKEYBOARD)Marshal.PtrToStructure(payload, typeof(RAWKEYBOARD));
                WriteLog("RAW-KBD", string.Format(
                    "dev={0} make=0x{1:X4} flags=0x{2:X4} vkey=0x{3:X2}({3}) key={4} msg={5} extra=0x{6:X8} fg=\"{7}\"",
                    device,
                    k.MakeCode,
                    k.Flags,
                    k.VKey,
                    KeyName(k.VKey),
                    KeyboardMessageName((int)k.Message),
                    k.ExtraInformation,
                    ForegroundTitle()));
            }
            else if (header.dwType == RIM_TYPEMOUSE)
            {
                RAWMOUSE mouse = (RAWMOUSE)Marshal.PtrToStructure(payload, typeof(RAWMOUSE));
                ushort buttonFlags = (ushort)(mouse.ulButtons & 0xFFFF);
                ushort buttonData = (ushort)((mouse.ulButtons >> 16) & 0xFFFF);

                // Do not flood the log with mouse movement. Record only buttons/wheel.
                if (buttonFlags != 0)
                {
                    WriteLog("RAW-MOUSE", string.Format(
                        "dev={0} mouseFlags=0x{1:X4} buttonFlags=0x{2:X4} buttonData=0x{3:X4} rawButtons=0x{4:X8} extra=0x{5:X8} fg=\"{6}\"",
                        device,
                        mouse.usFlags,
                        buttonFlags,
                        buttonData,
                        mouse.ulRawButtons,
                        mouse.ulExtraInformation,
                        ForegroundTitle()));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void InitializeSampledStates()
    {
        int[] keys = ImportantKeys();
        for (int i = 0; i < keys.Length; i++)
            sampledStates[keys[i]] = IsKeyDown(keys[i]);
    }

    private void SampleImportantKeyStates(object sender, EventArgs e)
    {
        int[] keys = ImportantKeys();
        for (int i = 0; i < keys.Length; i++)
        {
            int vk = keys[i];
            bool now = IsKeyDown(vk);
            bool old;
            if (!sampledStates.TryGetValue(vk, out old)) old = false;

            if (now != old)
            {
                sampledStates[vk] = now;
                WriteLog("STATE", string.Format("vk=0x{0:X2}({0}) key={1} => {2} fg=\"{3}\"",
                    vk, KeyName((uint)vk), now ? "DOWN" : "UP", ForegroundTitle()));
            }
        }
    }

    private static int[] ImportantKeys()
    {
        return new int[] { VK_CONTROL, VK_LCONTROL, VK_RCONTROL, VK_MENU, VK_LMENU, VK_RMENU, VK_0, VK_NONAME };
    }

    private static bool IsKeyDown(int vk)
    {
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    private string DeviceName(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "<zero>";

        string cached;
        if (deviceNames.TryGetValue(handle, out cached)) return cached;

        string name = "0x" + handle.ToInt64().ToString("X");
        try
        {
            uint chars = 0;
            GetRawInputDeviceInfoPtr(handle, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
            if (chars > 0)
            {
                StringBuilder sb = new StringBuilder((int)chars + 2);
                uint copy = chars;
                uint result = GetRawInputDeviceInfoString(handle, RIDI_DEVICENAME, sb, ref copy);
                if (result != 0xFFFFFFFF && sb.Length > 0)
                    name = sb.ToString();
            }
        }
        catch { }

        deviceNames[handle] = name;
        return name;
    }

    private void WriteLog(string source, string text)
    {
        string line = string.Format("{0:HH:mm:ss.fff} #{1:D5} [{2}] {3}", DateTime.Now, ++sequence, source, text);

        try
        {
            if (logBox != null && !logBox.IsDisposed)
            {
                if (logBox.InvokeRequired)
                    logBox.BeginInvoke((MethodInvoker)delegate { AppendLine(line); });
                else
                    AppendLine(line);
            }
        }
        catch { }

        try
        {
            if (writer != null)
            {
                lock (writer) { writer.WriteLine(line); }
            }
        }
        catch { }
    }

    private void AppendLine(string line)
    {
        logBox.AppendText(line + Environment.NewLine);
        if (logBox.TextLength > 500000)
        {
            logBox.Select(0, 100000);
            logBox.SelectedText = "";
        }
        logBox.SelectionStart = logBox.TextLength;
        logBox.ScrollToCaret();
    }

    private static string KeyboardMessageName(int msg)
    {
        if (msg == WM_KEYDOWN) return "WM_KEYDOWN";
        if (msg == WM_KEYUP) return "WM_KEYUP";
        if (msg == WM_SYSKEYDOWN) return "WM_SYSKEYDOWN";
        if (msg == WM_SYSKEYUP) return "WM_SYSKEYUP";
        return "0x" + msg.ToString("X4");
    }

    private static string KeyName(uint vk)
    {
        if (vk == VK_NONAME) return "VK_NONAME";
        try { return ((Keys)vk).ToString(); }
        catch { return "?"; }
    }

    private static string ForegroundTitle()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString().Replace("\r", " ").Replace("\n", " ");
        }
        catch { return ""; }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort usFlags;
        public uint ulButtons;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoPtr(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoString(IntPtr hDevice, uint uiCommand, StringBuilder pData, ref uint pcbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
