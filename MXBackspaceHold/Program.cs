using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Net;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Windows.Automation;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        bool createdNew;
        using (var mutex = new Mutex(true, @"Local\MXBackspaceHold_SingleInstance", out createdNew))
        {
            if (!createdNew)
            {
                MessageBox.Show("MXBackspaceHold 已经在后台运行。", "MXBackspaceHold",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        }
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN    = 0x0100;
    private const int WM_KEYUP      = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP   = 0x0105;

    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP   = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP   = 0x020C;

    private const byte VK_BACK      = 0x08;
    private const byte VK_RETURN    = 0x0D;
    private const byte VK_CONTROL   = 0x11;
    private const byte VK_MENU      = 0x12;
    private const byte VK_0         = 0x30;
    private const byte VK_LCONTROL  = 0xA2;
    private const byte VK_RCONTROL  = 0xA3;
    private const byte VK_LMENU     = 0xA4;
    private const byte VK_RMENU     = 0xA5;
    private const byte VK_NONAME   = 0xFC;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP       = 0x0002;

    private const uint SPI_GETKEYBOARDSPEED = 0x000A;
    private const uint SPI_GETKEYBOARDDELAY = 0x0016;

    private const string AppRegPath = @"Software\MXBackspaceHold";
    private const string RunRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MXBackspaceHold";

    // HapticWebPlugin：只使用一个轻微波形，作为语音开始/自动发送两个状态反馈。
    private const string HapticEndpoint = "https://local.jmw.nz:41443/haptic/subtle_collision";

    // 标记本程序自己注入的按键，防止键盘 Hook 再次把它当成 MX 语音触发组合。
    private const long SyntheticExtraInfoValue = 0x4D584248; // "MXBH"
    private const long WeTypeExtraInfoValue = 0x57545950;    // observed WeType marker
    private static readonly UIntPtr SyntheticExtraInfo = new UIntPtr((uint)SyntheticExtraInfoValue);

    private readonly NotifyIcon trayIcon;
    private readonly ToolStripMenuItem enabledItem;
    private readonly ToolStripMenuItem xButton1Item;
    private readonly ToolStripMenuItem xButton2Item;
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem detectedItem;

    private readonly ToolStripMenuItem speedMenu;
    private readonly ToolStripMenuItem delayMenu;

    private readonly ToolStripMenuItem speedWindowsItem;
    private readonly ToolStripMenuItem speed30Item;
    private readonly ToolStripMenuItem speed25Item;
    private readonly ToolStripMenuItem speed20Item;
    private readonly ToolStripMenuItem speed15Item;

    private readonly ToolStripMenuItem delayWindowsItem;
    private readonly ToolStripMenuItem delay400Item;
    private readonly ToolStripMenuItem delay300Item;
    private readonly ToolStripMenuItem delay250Item;

    private readonly ToolStripMenuItem voiceEnabledItem;
    private readonly ToolStripMenuItem voiceDelayMenu;
    private readonly ToolStripMenuItem voiceDelay500Item;
    private readonly ToolStripMenuItem voiceDelay800Item;
    private readonly ToolStripMenuItem voiceDelay1000Item;
    private readonly ToolStripMenuItem voiceDelay1500Item;
    private readonly ToolStripMenuItem voiceDelay2000Item;
    private readonly ToolStripMenuItem voiceKeepDraftHintItem;
    private readonly ToolStripMenuItem voiceStatusItem;

    private readonly LowLevelMouseProc mouseHookProc;
    private readonly LowLevelKeyboardProc keyboardHookProc;
    private IntPtr mouseHookHandle = IntPtr.Zero;
    private IntPtr keyboardHookHandle = IntPtr.Zero;

    private volatile bool enabled = true;
    private volatile bool held = false;
    private volatile int selectedButton = 2; // 1=XButton1, 2=XButton2

    // 0 = 跟随 Windows；否则为固定毫秒值
    private volatile int repeatIntervalOverride = 0;
    private volatile int repeatDelayOverride = 0;

    // 语音功能：MX Master 4 的目标实体键在 Options+ 里映射为 Alt+0。
    // 微信输入法自己处理 Alt+0 的按住/松开；本程序只旁观，不拦截、不重放快捷键。
    // 诊断日志显示：Alt 在实体键按住期间保持 Down；微信输入法同时产生
    // VK_NONAME(0xFC) / extraInfo=0x57545950 标记。看到该标记后进入语音状态，
    // 真正收到 Alt Up 时就等价于“鼠标语音键已松开”。
    private volatile bool voiceEnabled = true;
    private volatile bool voiceHeld = false;
    private volatile bool voiceKeepDraft = false;
    private volatile bool middleButtonCaptured = false;
    private volatile bool voiceAltDown = false;
    private volatile int voiceSendDelayMs = 800;
    private int voiceSequence = 0;
    private int voiceAttempt = 0;
    private int activeVoiceAttempt = 0;
    private long voiceHeldStartedTimestamp = 0;
    private readonly object voiceLogLock = new object();

    // Voice input validation: remember the GPT target and observe UIA text changes.
    // This diagnostic path does not change Enter behavior and never writes
    // intermediate text to the summary log.
    private readonly object voiceObservationLock = new object();
    private AutomationElement observedTargetElement;
    private AutomationEventHandler observedTextChangedHandler;
    private IntPtr savedTargetHwnd = IntPtr.Zero;
    private int savedTargetPid = 0;
    private string savedTargetProcess = string.Empty;
    private string savedTargetTitle = string.Empty;
    private int observedVoiceAttempt = 0;
    private int observedTextChangedCount = 0;
    private bool observedTargetAttached = false;
    private bool observedSummaryWritten = false;

    private System.Threading.Timer repeatTimer;

    public TrayContext()
    {
        LoadSettings();

        mouseHookProc = MouseHookCallback;
        keyboardHookProc = KeyboardHookCallback;

        mouseHookHandle = SetWindowsHookExMouse(WH_MOUSE_LL, mouseHookProc, GetModuleHandle(null), 0);
        if (mouseHookHandle == IntPtr.Zero)
        {
            MessageBox.Show(
                "无法安装鼠标监听。\r\n\r\n请退出后重新运行；如果目标程序以管理员身份运行，也可以尝试以管理员身份运行本程序。",
                "MXBackspaceHold",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            ExitThread();
            return;
        }

        keyboardHookHandle = SetWindowsHookExKeyboard(WH_KEYBOARD_LL, keyboardHookProc, GetModuleHandle(null), 0);
        if (keyboardHookHandle == IntPtr.Zero)
        {
            UnhookWindowsHookEx(mouseHookHandle);
            mouseHookHandle = IntPtr.Zero;
            MessageBox.Show(
                "无法安装键盘监听。\r\n\r\nAlt+0 语音功能无法工作。请退出后重新运行；如果目标程序以管理员身份运行，也可以尝试以管理员身份运行本程序。",
                "MXBackspaceHold",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            ExitThread();
            return;
        }

        repeatTimer = new System.Threading.Timer(RepeatBackspace, null, Timeout.Infinite, Timeout.Infinite);

        enabledItem = new ToolStripMenuItem("启用连续退格");
        enabledItem.Checked = enabled;
        enabledItem.CheckOnClick = true;
        enabledItem.Click += delegate
        {
            enabled = enabledItem.Checked;
            if (!enabled)
                StopRepeating();
            SaveSettings();
        };

        xButton1Item = new ToolStripMenuItem("使用 XButton1（通常是“后退”）");
        xButton2Item = new ToolStripMenuItem("使用 XButton2（通常是“前进”）");

        xButton1Item.Click += delegate { SelectButton(1); };
        xButton2Item.Click += delegate { SelectButton(2); };

        detectedItem = new ToolStripMenuItem("最近检测到：尚未按侧键");
        detectedItem.Enabled = false;

        startupItem = new ToolStripMenuItem("开机自动启动");
        startupItem.Checked = IsStartupEnabled();
        startupItem.Click += delegate
        {
            try
            {
                SetStartupEnabled(!IsStartupEnabled());
                startupItem.Checked = IsStartupEnabled();
            }
            catch (Exception ex)
            {
                MessageBox.Show("修改开机启动失败：\r\n" + ex.Message,
                    "MXBackspaceHold", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        speedMenu = new ToolStripMenuItem("重复速度");
        speedWindowsItem = new ToolStripMenuItem("跟随 Windows");
        speed30Item = new ToolStripMenuItem("快：30 ms / 次");
        speed25Item = new ToolStripMenuItem("更快：25 ms / 次");
        speed20Item = new ToolStripMenuItem("很快：20 ms / 次");
        speed15Item = new ToolStripMenuItem("极速：15 ms / 次");

        speedWindowsItem.Click += delegate { SetRepeatInterval(0); };
        speed30Item.Click += delegate { SetRepeatInterval(30); };
        speed25Item.Click += delegate { SetRepeatInterval(25); };
        speed20Item.Click += delegate { SetRepeatInterval(20); };
        speed15Item.Click += delegate { SetRepeatInterval(15); };

        speedMenu.DropDownItems.Add(speedWindowsItem);
        speedMenu.DropDownItems.Add(speed30Item);
        speedMenu.DropDownItems.Add(speed25Item);
        speedMenu.DropDownItems.Add(speed20Item);
        speedMenu.DropDownItems.Add(speed15Item);

        delayMenu = new ToolStripMenuItem("长按延迟");
        delayWindowsItem = new ToolStripMenuItem("跟随 Windows");
        delay400Item = new ToolStripMenuItem("400 ms");
        delay300Item = new ToolStripMenuItem("300 ms");
        delay250Item = new ToolStripMenuItem("250 ms");

        delayWindowsItem.Click += delegate { SetRepeatDelay(0); };
        delay400Item.Click += delegate { SetRepeatDelay(400); };
        delay300Item.Click += delegate { SetRepeatDelay(300); };
        delay250Item.Click += delegate { SetRepeatDelay(250); };

        delayMenu.DropDownItems.Add(delayWindowsItem);
        delayMenu.DropDownItems.Add(delay400Item);
        delayMenu.DropDownItems.Add(delay300Item);
        delayMenu.DropDownItems.Add(delay250Item);

        voiceEnabledItem = new ToolStripMenuItem("启用 Alt+0 长按语音自动发送");
        voiceEnabledItem.Checked = voiceEnabled;
        voiceEnabledItem.CheckOnClick = true;
        voiceEnabledItem.Click += delegate
        {
            voiceEnabled = voiceEnabledItem.Checked;
            if (!voiceEnabled)
                ResetVoiceState();
            SaveSettings();
            UpdateVoiceStatus(voiceEnabled ? "语音：待机" : "语音：已关闭");
        };

        voiceDelayMenu = new ToolStripMenuItem("语音自动发送延迟");
        voiceDelay500Item = new ToolStripMenuItem("500 ms");
        voiceDelay800Item = new ToolStripMenuItem("800 ms（推荐先试）");
        voiceDelay1000Item = new ToolStripMenuItem("1000 ms");
        voiceDelay1500Item = new ToolStripMenuItem("1500 ms");
        voiceDelay2000Item = new ToolStripMenuItem("2000 ms");

        voiceDelay500Item.Click += delegate { SetVoiceSendDelay(500); };
        voiceDelay800Item.Click += delegate { SetVoiceSendDelay(800); };
        voiceDelay1000Item.Click += delegate { SetVoiceSendDelay(1000); };
        voiceDelay1500Item.Click += delegate { SetVoiceSendDelay(1500); };
        voiceDelay2000Item.Click += delegate { SetVoiceSendDelay(2000); };

        voiceDelayMenu.DropDownItems.Add(voiceDelay500Item);
        voiceDelayMenu.DropDownItems.Add(voiceDelay800Item);
        voiceDelayMenu.DropDownItems.Add(voiceDelay1000Item);
        voiceDelayMenu.DropDownItems.Add(voiceDelay1500Item);
        voiceDelayMenu.DropDownItems.Add(voiceDelay2000Item);

        voiceKeepDraftHintItem = new ToolStripMenuItem("按住语音键时点一下滚轮：本轮只结束，不自动发送");
        voiceKeepDraftHintItem.Enabled = false;

        voiceStatusItem = new ToolStripMenuItem(voiceEnabled ? "语音：待机" : "语音：已关闭");
        voiceStatusItem.Enabled = false;

        UpdateSpeedChecks();
        UpdateDelayChecks();
        UpdateVoiceDelayChecks();

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += delegate { ExitThread(); };

        UpdateButtonChecks();

        var menu = new ContextMenuStrip();
        menu.Items.Add(enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(xButton1Item);
        menu.Items.Add(xButton2Item);
        menu.Items.Add(detectedItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(speedMenu);
        menu.Items.Add(delayMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(voiceEnabledItem);
        menu.Items.Add(voiceDelayMenu);
        menu.Items.Add(voiceKeepDraftHintItem);
        menu.Items.Add(voiceStatusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        trayIcon = new NotifyIcon();
        trayIcon.Icon = SystemIcons.Application;
        trayIcon.Text = "MXBackspaceHold v1.4.3";
        trayIcon.ContextMenuStrip = menu;
        trayIcon.Visible = true;
        trayIcon.DoubleClick += delegate
        {
            enabled = !enabled;
            enabledItem.Checked = enabled;
            if (!enabled)
                StopRepeating();
            SaveSettings();
            trayIcon.ShowBalloonTip(
                1200,
                "MXBackspaceHold",
                enabled ? "连续退格已启用" : "连续退格已暂停",
                ToolTipIcon.Info);
        };

        trayIcon.ShowBalloonTip(
            2200,
            "MXBackspaceHold v1.4.3 已启动",
            "连续退格保持原样；MX 语音键请映射为 Alt+0。语音开始和自动发送各震一下 subtle_collision；中键保留文字时不会触发发送震动。",
            ToolTipIcon.Info);
    }

    private void SelectButton(int button)
    {
        StopRepeating();
        selectedButton = button;
        UpdateButtonChecks();
        SaveSettings();
    }

    private void UpdateButtonChecks()
    {
        if (xButton1Item != null)
            xButton1Item.Checked = selectedButton == 1;
        if (xButton2Item != null)
            xButton2Item.Checked = selectedButton == 2;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // 语音期间：滚轮“旋转”完全放行；只有“按下滚轮中键”才作为本轮保留草稿标记。
            if (voiceEnabled)
            {
                if (wParam == (IntPtr)WM_MBUTTONDOWN && voiceHeld)
                {
                    voiceKeepDraft = true;
                    middleButtonCaptured = true;
                    UpdateVoiceStatus("语音：本轮保留，不自动发送");
                    return (IntPtr)1;
                }

                if (wParam == (IntPtr)WM_MBUTTONUP && middleButtonCaptured)
                {
                    middleButtonCaptured = false;
                    return (IntPtr)1;
                }
            }

            if (enabled &&
                (wParam == (IntPtr)WM_XBUTTONDOWN || wParam == (IntPtr)WM_XBUTTONUP))
            {
                MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                    lParam, typeof(MSLLHOOKSTRUCT));

                int button = (int)((data.mouseData >> 16) & 0xFFFF);

                if (wParam == (IntPtr)WM_XBUTTONDOWN && (button == 1 || button == 2))
                {
                    try
                    {
                        if (detectedItem != null && detectedItem.GetCurrentParent() != null)
                        {
                            detectedItem.GetCurrentParent().BeginInvoke((MethodInvoker)delegate
                            {
                                detectedItem.Text = "最近检测到：XButton" + button;
                            });
                        }
                    }
                    catch { }
                }

                if (button == selectedButton)
                {
                    if (wParam == (IntPtr)WM_XBUTTONDOWN)
                    {
                        if (!held)
                        {
                            held = true;

                            // 像实体 Backspace：按下立即删一个
                            SendBackspace();

                            int delayMs = repeatDelayOverride > 0
                                ? repeatDelayOverride
                                : GetKeyboardRepeatDelayMs();

                            int intervalMs = repeatIntervalOverride > 0
                                ? repeatIntervalOverride
                                : GetKeyboardRepeatIntervalMs();

                            repeatTimer.Change(delayMs, intervalMs);
                        }
                    }
                    else
                    {
                        StopRepeating();
                    }

                    // 拦截这个侧键，避免同时触发浏览器“前进/后退”
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(mouseHookHandle, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && voiceEnabled)
        {
            KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                lParam, typeof(KBDLLHOOKSTRUCT));

            // 本程序自己补发的 Enter 只放行，不参与语音状态判断。
            if (data.dwExtraInfo.ToInt64() == SyntheticExtraInfoValue)
                return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);

            bool isDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
            bool isUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;
            bool isAlt = IsAltKey(data.vkCode);

            if (isAlt)
            {
                if (isDown)
                {
                    if (!voiceAltDown)
                    {
                        activeVoiceAttempt = Interlocked.Increment(ref voiceAttempt);
                        LogVoice(activeVoiceAttempt, "ALT_DOWN");
                    }
                    voiceAltDown = true;
                }
                else if (isUp)
                {
                    bool heldAtRelease = voiceHeld;
                    long durationMs = -1;
                    if (heldAtRelease && voiceHeldStartedTimestamp > 0)
                        durationMs = (long)((Stopwatch.GetTimestamp() - voiceHeldStartedTimestamp) * 1000.0 / Stopwatch.Frequency);
                    LogVoice(activeVoiceAttempt, "ALT_UP held=" + (heldAtRelease ? "1" : "0") + " duration=" + durationMs + "ms keepDraft=" + (voiceKeepDraft ? "1" : "0"));
                    voiceAltDown = false;

                    // Alt+0 的 Alt Up 就是 Options+ 实体语音键真正松开的时刻。
                    // 不吞这个 Up：先让微信输入法原生结束语音，再由我们延迟一次 Enter 发送。
                    if (voiceHeld)
                    {
                        bool keepDraft = voiceKeepDraft;
                        int attempt = activeVoiceAttempt;
                        voiceHeld = false;
                        voiceKeepDraft = false;
                        voiceHeldStartedTimestamp = 0;

                        int sequence = Interlocked.Increment(ref voiceSequence);
                        BeginFinishVoiceSession(sequence, keepDraft, attempt);
                    }
                }

                return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);
            }

            bool isWeTypeMarker =
                data.vkCode == VK_NONAME &&
                data.dwExtraInfo.ToInt64() == WeTypeExtraInfoValue;

            // 正常情况下 Options+/微信输入法会把“0”变成诊断中观察到的 VK_NONAME 标记；
            // 同时保留标准 VK_0 作为兼容兜底。这里只识别，不拦截，让微信原生逻辑完整运行。
            bool isVoiceTrigger = isWeTypeMarker || data.vkCode == VK_0;
            if (isDown && isVoiceTrigger)
                LogVoice(activeVoiceAttempt, "TRIGGER vk=0x" + data.vkCode.ToString("X2") + " extra=0x" + data.dwExtraInfo.ToInt64().ToString("X") + " alt=" + (voiceAltDown ? "1" : "0"));

            if (!voiceHeld && isDown && voiceAltDown && isVoiceTrigger)
            {
                Interlocked.Increment(ref voiceSequence);
                voiceHeld = true;
                voiceHeldStartedTimestamp = Stopwatch.GetTimestamp();
                BeginVoiceTargetObservation(activeVoiceAttempt);
                LogVoice(activeVoiceAttempt, "VOICE_HELD");
                voiceKeepDraft = false;
                middleButtonCaptured = false;
                UpdateVoiceStatus("语音：按住说话中");
                TryHaptic();
            }
        }

        return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);
    }

    private static bool IsAltKey(uint vkCode)
    {
        return vkCode == VK_MENU || vkCode == VK_LMENU || vkCode == VK_RMENU;
    }

    private void BeginFinishVoiceSession(int sequence, bool keepDraft, int attempt)
    {
        if (!keepDraft)
            LogVoice(attempt, "SEND_QUEUED seq=" + sequence + " delay=" + voiceSendDelayMs + "ms");

        UpdateVoiceStatus(keepDraft ? "语音：已松开，保留文字" : "语音：已松开，等待自动发送");

        ThreadPool.QueueUserWorkItem(delegate
        {
            // Alt Up 已经被原样放给微信输入法，因此微信会自己结束长语音并做最后转写。
            // 保留草稿时不需要再发任何 Enter。
            if (keepDraft)
            {
                WriteVoiceObservationSummary(attempt, sequence, "KEEP_DRAFT");
                EndVoiceTargetObservation(attempt, "KEEP_DRAFT");
                UpdateVoiceStatus("语音：已保留文字，待机");
                return;
            }

            // 只等一次排版/落字延迟，然后发一个 Enter 发送消息。
            Thread.Sleep(voiceSendDelayMs);

            LogVoice(attempt, "SEND_CHECK seq=" + sequence + " current=" + Volatile.Read(ref voiceSequence) + " enabled=" + (voiceEnabled ? "1" : "0"));

            if (sequence != Volatile.Read(ref voiceSequence) || !voiceEnabled)
            {
                EndVoiceTargetObservation(attempt, "CANCELLED");
                return;
            }

            WriteVoiceObservationSummary(attempt, sequence, "BEFORE_ENTER");
            EndVoiceTargetObservation(attempt, "BEFORE_ENTER");
            SendEnter();
            LogVoice(attempt, "ENTER_SENT");
            TryHaptic();
            UpdateVoiceStatus("语音：已发送，待机");
        });
    }

    private void ResetVoiceState()
    {
        EndVoiceTargetObservation(0, "RESET");
        Interlocked.Increment(ref voiceSequence);
        voiceHeld = false;
        voiceKeepDraft = false;
        middleButtonCaptured = false;
        voiceAltDown = false;
    }

    private void LogVoice(int attempt, string message)
    {
        try
        {
            lock (voiceLogLock)
            {
                string logPath = System.IO.Path.Combine(Application.StartupPath, "voice.log");
                System.IO.File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " attempt=" + attempt + " " + message + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    private void BeginVoiceTargetObservation(int attempt)
    {
        EndVoiceTargetObservation(attempt, "NEW_SESSION");

        IntPtr hwnd = GetForegroundWindow();
        uint pidValue = 0;
        if (hwnd != IntPtr.Zero)
            GetWindowThreadProcessId(hwnd, out pidValue);

        string processName = string.Empty;
        try
        {
            if (pidValue != 0)
                processName = Process.GetProcessById((int)pidValue).ProcessName;
        }
        catch { }

        string title = GetWindowTitle(hwnd);
        AutomationElement focused = null;
        string attachReason = string.Empty;
        try
        {
            focused = AutomationElement.FocusedElement;
            if (focused == null)
                attachReason = "focused_null";
            else if (focused.Current.ProcessId != (int)pidValue)
                attachReason = "focused_pid_mismatch";
            else if (focused.Current.ControlType != ControlType.Edit)
                attachReason = "focused_not_edit";
        }
        catch (Exception ex)
        {
            attachReason = ex.GetType().Name;
            focused = null;
        }

        lock (voiceObservationLock)
        {
            savedTargetHwnd = hwnd;
            savedTargetPid = (int)pidValue;
            savedTargetProcess = processName;
            savedTargetTitle = title;
            observedVoiceAttempt = attempt;
            observedTextChangedCount = 0;
            observedSummaryWritten = false;
            observedTargetElement = focused;
            observedTargetAttached = false;
        }

        LogVoice(attempt, "TARGET_SAVED hwnd=0x" + hwnd.ToInt64().ToString("X") +
            " pid=" + pidValue +
            " process=" + EscapeLogValue(processName) +
            " title=\"" + EscapeLogValue(title) + "\"");

        if (focused == null)
        {
            LogVoice(attempt, "UIA_OBSERVATION_ATTACHED=0 reason=" + EscapeLogValue(attachReason));
            return;
        }

        try
        {
            AutomationEventHandler handler = delegate(object sender, AutomationEventArgs args)
            {
                OnObservedTextChanged(attempt);
            };

            Automation.AddAutomationEventHandler(
                TextPattern.TextChangedEvent,
                focused,
                TreeScope.Element,
                handler);

            lock (voiceObservationLock)
            {
                observedTextChangedHandler = handler;
                observedTargetAttached = true;
            }
            LogVoice(attempt, "UIA_OBSERVATION_ATTACHED=1 event=TextChanged");
        }
        catch (Exception ex)
        {
            LogVoice(attempt, "UIA_OBSERVATION_ATTACHED=0 reason=" + ex.GetType().Name);
        }
    }

    private void OnObservedTextChanged(int attempt)
    {
        int count;
        lock (voiceObservationLock)
        {
            if (attempt != observedVoiceAttempt)
                return;
            observedTextChangedCount++;
            count = observedTextChangedCount;
        }

        // Do not read or log the full text for every event.
        LogVoice(attempt, "UIA_TEXT_CHANGED count=" + count);
    }

    private void WriteVoiceObservationSummary(int attempt, int sequence, string reason)
    {
        AutomationElement element;
        IntPtr hwnd;
        int pid;
        string process;
        string title;
        int eventCount;

        lock (voiceObservationLock)
        {
            if (attempt != observedVoiceAttempt || observedSummaryWritten)
                return;
            observedSummaryWritten = true;
            element = observedTargetElement;
            hwnd = savedTargetHwnd;
            pid = savedTargetPid;
            process = savedTargetProcess;
            title = savedTargetTitle;
            eventCount = observedTextChangedCount;
        }

        string text = ReadAutomationText(element);
        bool usable = IsUsableInputText(text);
        string summaryPath = Path.Combine(Application.StartupPath, "voice-summary.jsonl");
        string line = "{\"attempt\":" + attempt +
            ",\"sequence\":" + sequence +
            ",\"reason\":\"" + JsonEscape(reason) + "\"" +
            ",\"target\":{\"hwnd\":\"0x" + hwnd.ToInt64().ToString("X") +
            "\",\"pid\":" + pid +
            ",\"process\":\"" + JsonEscape(process) +
            "\",\"title\":\"" + JsonEscape(title) + "\"}" +
            ",\"textChangedEvents\":" + eventCount +
            ",\"readOk\":" + (usable ? "true" : "false") +
            ",\"textLength\":" + text.Length +
            ",\"finalText\":\"" + JsonEscape(text) + "\"}";

        try
        {
            lock (voiceLogLock)
            {
                File.AppendAllText(summaryPath, line + Environment.NewLine, Encoding.UTF8);
            }
            LogVoice(attempt, "UIA_FINAL_READ reason=" + reason +
                " events=" + eventCount +
                " readOk=" + (usable ? "1" : "0") +
                " len=" + text.Length +
                " summary=voice-summary.jsonl");
        }
        catch (Exception ex)
        {
            LogVoice(attempt, "UIA_FINAL_READ_FAILED reason=" + ex.GetType().Name);
        }
    }

    private void EndVoiceTargetObservation(int attempt, string reason)
    {
        AutomationElement element;
        AutomationEventHandler handler;
        bool attached;

        lock (voiceObservationLock)
        {
            element = observedTargetElement;
            handler = observedTextChangedHandler;
            attached = observedTargetAttached;
            observedTargetElement = null;
            observedTextChangedHandler = null;
            observedTargetAttached = false;
        }

        if (attached && element != null && handler != null)
        {
            try
            {
                Automation.RemoveAutomationEventHandler(
                    TextPattern.TextChangedEvent,
                    element,
                    handler);
            }
            catch { }
        }

        if (attempt != 0)
            LogVoice(attempt, "UIA_OBSERVATION_STOP reason=" + EscapeLogValue(reason));
    }

    private static string ReadAutomationText(AutomationElement element)
    {
        if (element == null)
            return string.Empty;

        try
        {
            object patternObject;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out patternObject))
            {
                string value = ((ValuePattern)patternObject).Current.Value ?? string.Empty;
                if (value.Length > 0)
                    return value;
            }

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out patternObject))
            {
                string value = ((TextPattern)patternObject).DocumentRange.GetText(-1) ?? string.Empty;
                if (value.Length > 0)
                    return value;
            }

            AutomationElementCollection children = element.FindAll(
                TreeScope.Children, Condition.TrueCondition);
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < children.Count; i++)
            {
                AutomationElement child = children[i];
                if (child.TryGetCurrentPattern(TextPattern.Pattern, out patternObject))
                    builder.Append(((TextPattern)patternObject).DocumentRange.GetText(-1));
            }
            return builder.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsUsableInputText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        string normalized = text.Trim('\r', '\n', ' ', '\t');
        return normalized.Length > 0 &&
            !string.Equals(normalized, "使用 ChatGPT Work", StringComparison.Ordinal);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return string.Empty;
        StringBuilder builder = new StringBuilder(256);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string EscapeLogValue(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\")
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"");
    }

    private static string JsonEscape(string value)
    {
        return EscapeLogValue(value);
    }

    private void UpdateVoiceStatus(string text)
    {
        try
        {
            if (voiceStatusItem == null)
                return;

            ToolStrip parent = voiceStatusItem.GetCurrentParent();
            if (parent != null && parent.InvokeRequired)
            {
                parent.BeginInvoke((MethodInvoker)delegate
                {
                    if (voiceStatusItem != null)
                        voiceStatusItem.Text = text;
                });
            }
            else
            {
                voiceStatusItem.Text = text;
            }
        }
        catch
        {
            // 状态文字仅用于观察，不影响核心功能。
        }
    }

    private void RepeatBackspace(object state)
    {
        if (enabled && held)
            SendBackspace();
    }

    private void StopRepeating()
    {
        held = false;
        if (repeatTimer != null)
            repeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private static int GetKeyboardRepeatDelayMs()
    {
        uint value;
        if (SystemParametersInfo(SPI_GETKEYBOARDDELAY, 0, out value, 0))
        {
            if (value > 3) value = 3;
            return (int)((value + 1) * 250); // Windows: 250/500/750/1000 ms
        }
        return 500;
    }

    private static int GetKeyboardRepeatIntervalMs()
    {
        uint speed;
        if (SystemParametersInfo(SPI_GETKEYBOARDSPEED, 0, out speed, 0))
        {
            if (speed > 31) speed = 31;

            // Windows 键盘速度约为 2.5 ~ 30 字符/秒
            double charsPerSecond = 2.5 + (27.5 * speed / 31.0);
            int interval = (int)Math.Round(1000.0 / charsPerSecond);
            if (interval < 20) interval = 20;
            return interval;
        }
        return 40;
    }

    private static void SendBackspace()
    {
        keybd_event(VK_BACK, 0, 0, SyntheticExtraInfo);
        keybd_event(VK_BACK, 0, KEYEVENTF_KEYUP, SyntheticExtraInfo);
    }

    private static void SendEnter()
    {
        keybd_event(VK_RETURN, 0, 0, SyntheticExtraInfo);
        keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, SyntheticExtraInfo);
    }

    private static void TryHaptic()
    {
        // 震动是纯旁路反馈：永远不在 Hook/发送线程里等待网络。
        // 插件未启动、鼠标未连接或请求失败时直接忽略，不影响语音和连续退格。
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(HapticEndpoint);
                request.Method = "POST";
                request.ContentLength = 0;
                request.Timeout = 500;
                request.ReadWriteTimeout = 500;
                request.KeepAlive = false;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    // 收到响应即可；不读取/保存日志，避免额外状态和长期内存占用。
                }
            }
            catch
            {
                // 触觉反馈失败绝不能影响核心功能。
            }
        });
    }

    private void SetRepeatInterval(int intervalMs)
    {
        repeatIntervalOverride = intervalMs;
        UpdateSpeedChecks();
        SaveSettings();

        // 如果当前正处于长按连删，立即应用新速度
        if (held && repeatTimer != null)
        {
            int delayMs = 1;
            int interval = repeatIntervalOverride > 0
                ? repeatIntervalOverride
                : GetKeyboardRepeatIntervalMs();

            repeatTimer.Change(delayMs, interval);
        }
    }

    private void SetRepeatDelay(int delayMs)
    {
        repeatDelayOverride = delayMs;
        UpdateDelayChecks();
        SaveSettings();
    }

    private void SetVoiceSendDelay(int delayMs)
    {
        voiceSendDelayMs = delayMs;
        UpdateVoiceDelayChecks();
        SaveSettings();
    }

    private void UpdateSpeedChecks()
    {
        if (speedWindowsItem != null) speedWindowsItem.Checked = repeatIntervalOverride == 0;
        if (speed30Item != null) speed30Item.Checked = repeatIntervalOverride == 30;
        if (speed25Item != null) speed25Item.Checked = repeatIntervalOverride == 25;
        if (speed20Item != null) speed20Item.Checked = repeatIntervalOverride == 20;
        if (speed15Item != null) speed15Item.Checked = repeatIntervalOverride == 15;
    }

    private void UpdateDelayChecks()
    {
        if (delayWindowsItem != null) delayWindowsItem.Checked = repeatDelayOverride == 0;
        if (delay400Item != null) delay400Item.Checked = repeatDelayOverride == 400;
        if (delay300Item != null) delay300Item.Checked = repeatDelayOverride == 300;
        if (delay250Item != null) delay250Item.Checked = repeatDelayOverride == 250;
    }

    private void UpdateVoiceDelayChecks()
    {
        if (voiceDelay500Item != null) voiceDelay500Item.Checked = voiceSendDelayMs == 500;
        if (voiceDelay800Item != null) voiceDelay800Item.Checked = voiceSendDelayMs == 800;
        if (voiceDelay1000Item != null) voiceDelay1000Item.Checked = voiceSendDelayMs == 1000;
        if (voiceDelay1500Item != null) voiceDelay1500Item.Checked = voiceSendDelayMs == 1500;
        if (voiceDelay2000Item != null) voiceDelay2000Item.Checked = voiceSendDelayMs == 2000;
    }

    private void LoadSettings()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AppRegPath))
            {
                object b = key.GetValue("SelectedButton", 2);
                object e = key.GetValue("Enabled", 1);
                object s = key.GetValue("RepeatIntervalOverride", 0);
                object d = key.GetValue("RepeatDelayOverride", 0);
                object ve = key.GetValue("VoiceEnabled", 1);
                object vd = key.GetValue("VoiceSendDelayMs", 800);

                int parsedButton;
                if (int.TryParse(Convert.ToString(b), out parsedButton) &&
                    (parsedButton == 1 || parsedButton == 2))
                    selectedButton = parsedButton;

                int parsedEnabled;
                if (int.TryParse(Convert.ToString(e), out parsedEnabled))
                    enabled = parsedEnabled != 0;

                int parsedSpeed;
                if (int.TryParse(Convert.ToString(s), out parsedSpeed) &&
                    (parsedSpeed == 0 || parsedSpeed == 30 || parsedSpeed == 25 ||
                     parsedSpeed == 20 || parsedSpeed == 15))
                    repeatIntervalOverride = parsedSpeed;

                int parsedDelay;
                if (int.TryParse(Convert.ToString(d), out parsedDelay) &&
                    (parsedDelay == 0 || parsedDelay == 400 ||
                     parsedDelay == 300 || parsedDelay == 250))
                    repeatDelayOverride = parsedDelay;

                int parsedVoiceEnabled;
                if (int.TryParse(Convert.ToString(ve), out parsedVoiceEnabled))
                    voiceEnabled = parsedVoiceEnabled != 0;

                int parsedVoiceDelay;
                if (int.TryParse(Convert.ToString(vd), out parsedVoiceDelay) &&
                    (parsedVoiceDelay == 500 || parsedVoiceDelay == 800 ||
                     parsedVoiceDelay == 1000 || parsedVoiceDelay == 1500 ||
                     parsedVoiceDelay == 2000))
                    voiceSendDelayMs = parsedVoiceDelay;
            }
        }
        catch
        {
            selectedButton = 2;
            enabled = true;
            repeatIntervalOverride = 0;
            repeatDelayOverride = 0;
            voiceEnabled = true;
            voiceSendDelayMs = 800;
        }
    }

    private void SaveSettings()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AppRegPath))
            {
                key.SetValue("SelectedButton", selectedButton, RegistryValueKind.DWord);
                key.SetValue("Enabled", enabled ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("RepeatIntervalOverride", repeatIntervalOverride, RegistryValueKind.DWord);
                key.SetValue("RepeatDelayOverride", repeatDelayOverride, RegistryValueKind.DWord);
                key.SetValue("VoiceEnabled", voiceEnabled ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("VoiceSendDelayMs", voiceSendDelayMs, RegistryValueKind.DWord);
            }
        }
        catch
        {
            // 设置保存失败不影响核心功能
        }
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegPath, false))
            {
                if (key == null) return false;
                string value = key.GetValue(RunValueName) as string;
                if (string.IsNullOrEmpty(value)) return false;

                string expected = "\"" + Application.ExecutablePath + "\"";
                return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }
    }

    private static void SetStartupEnabled(bool value)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunRegPath))
        {
            if (value)
                key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
            else
                key.DeleteValue(RunValueName, false);
        }
    }

    protected override void ExitThreadCore()
    {
        StopRepeating();
        ResetVoiceState();

        if (mouseHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(mouseHookHandle);
            mouseHookHandle = IntPtr.Zero;
        }

        if (keyboardHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(keyboardHookHandle);
            keyboardHookHandle = IntPtr.Zero;
        }

        if (repeatTimer != null)
        {
            repeatTimer.Dispose();
            repeatTimer = null;
        }

        if (trayIcon != null)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
        }

        base.ExitThreadCore();
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookEx")]
    private static extern IntPtr SetWindowsHookExMouse(
        int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookEx")]
    private static extern IntPtr SetWindowsHookExKeyboard(
        int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(
        IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction, uint uiParam, out uint pvParam, uint fWinIni);
}
