from pathlib import Path

ROOT = Path(__file__).resolve().parent
program = ROOT / "Program.cs"
readme = ROOT / "README.md"
usage = ROOT / "使用说明.txt"

s = program.read_text(encoding="utf-8-sig")

if "voiceChordCaptured" not in s:
    s = s.replace(
        "    private const byte VK_BACK     = 0x08;\n    private const byte VK_RETURN   = 0x0D;\n    private const byte VK_RCONTROL = 0xA3;\n",
        "    private const byte VK_BACK      = 0x08;\n"
        "    private const byte VK_RETURN    = 0x0D;\n"
        "    private const byte VK_CONTROL   = 0x11;\n"
        "    private const byte VK_MENU      = 0x12;\n"
        "    private const byte VK_0         = 0x30;\n"
        "    private const byte VK_LCONTROL  = 0xA2;\n"
        "    private const byte VK_RCONTROL  = 0xA3;\n"
        "    private const byte VK_LMENU     = 0xA4;\n"
        "    private const byte VK_RMENU     = 0xA5;\n"
    )

    s = s.replace(
        "// 标记本程序自己注入的按键，防止键盘 Hook 再次把它当成实体右 Ctrl。",
        "// 标记本程序自己注入的按键，防止键盘 Hook 再次把它当成 MX 语音触发组合。"
    )

    old_state = '''    // 语音功能：MX Master 4 的目标实体键在 Options+ 里直接映射为“右 Ctrl”。
    // 本程序吞掉实体右 Ctrl，按下时只向系统补发一次“右 Ctrl 点按”来启动微信长语音，
    // 因此用户按住说话期间 Ctrl 不会一直压在系统里，滚轮不会变成 Ctrl+滚轮缩放。
    private volatile bool voiceEnabled = true;
    private volatile bool voiceHeld = false;
    private volatile bool voiceKeepDraft = false;
    private volatile bool middleButtonCaptured = false;
    private volatile int voiceSendDelayMs = 800;
    private int voiceSequence = 0;
'''
    new_state = '''    // 语音功能：MX Master 4 的目标实体键在 Options+ 里映射为 Ctrl+Alt+0。
    // 这个组合只作为“鼠标语音键”的身份证：检测到后立即把系统里的 Ctrl/Alt 释放，
    // 再补发一次极短的 Ctrl+Alt+0 来启动微信长语音。这样手指继续按住鼠标时，
    // Windows 不会一直处于 Ctrl/Alt 按下状态，滚轮和点击仍保持普通行为。
    private volatile bool voiceEnabled = true;
    private volatile bool voiceHeld = false;
    private volatile bool voiceKeepDraft = false;
    private volatile bool middleButtonCaptured = false;
    private volatile bool voiceCtrlDown = false;
    private volatile bool voiceAltDown = false;
    private volatile bool voiceZeroDown = false;
    private volatile bool voiceChordCaptured = false;
    private volatile int voiceSendDelayMs = 800;
    private int voiceSequence = 0;
'''
    if old_state not in s:
        raise RuntimeError("v1.4 voice state block not found")
    s = s.replace(old_state, new_state)

    start = s.index("    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)")
    end = s.index("    private void BeginFinishVoiceSession", start)
    new_callback = r'''    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && voiceEnabled)
        {
            KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                lParam, typeof(KBDLLHOOKSTRUCT));

            // 本程序自己补发的按键必须放行，否则会被自己的 Hook 再次吞掉。
            if (data.dwExtraInfo.ToInt64() == SyntheticExtraInfoValue)
                return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);

            bool isDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
            bool isUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;
            bool isCtrl = IsCtrlKey(data.vkCode);
            bool isAlt = IsAltKey(data.vkCode);
            bool isZero = data.vkCode == VK_0;

            // 先记录实体组合键状态。合成事件在上面已经放行，不会污染这里的状态。
            if (isCtrl)
            {
                if (isDown) voiceCtrlDown = true;
                if (isUp) voiceCtrlDown = false;
            }
            else if (isAlt)
            {
                if (isDown) voiceAltDown = true;
                if (isUp) voiceAltDown = false;
            }
            else if (isZero)
            {
                if (isDown) voiceZeroDown = true;
                if (isUp) voiceZeroDown = false;
            }

            // Ctrl 和 Alt 的最初 KeyDown 会先正常到达系统；直到 0 Down 出现，
            // 我们才知道这是专用的 Ctrl+Alt+0 语音组合，而不是用户正常使用 Ctrl/Alt。
            if (!voiceChordCaptured && isDown && isZero && voiceCtrlDown && voiceAltDown)
            {
                Interlocked.Increment(ref voiceSequence);
                voiceHeld = true;
                voiceKeepDraft = false;
                middleButtonCaptured = false;
                voiceChordCaptured = true;

                UpdateVoiceStatus("语音：按住说话中");

                // 吞掉实体 0 Down，并立即把刚刚进入系统的 Ctrl/Alt 中和掉；
                // 然后补发一次极短的 Ctrl+Alt+0 点按来启动微信长语音。
                // 之后即使 MX 侧键仍持续按住，系统也没有 Ctrl/Alt 修饰状态，滚轮可正常滚动。
                SendCtrlAlt0TapAndNeutralize();
                return (IntPtr)1;
            }

            if (voiceChordCaptured && (isCtrl || isAlt || isZero))
            {
                // 组合已经被本程序接管后，后续重复 Down/Up 都不再交给前台程序。
                // 等实体 Ctrl、Alt、0 全部真正松开，才视为“鼠标语音键已松开”。
                if (isUp && !voiceCtrlDown && !voiceAltDown && !voiceZeroDown)
                {
                    bool keepDraft = voiceKeepDraft;
                    voiceHeld = false;
                    voiceKeepDraft = false;
                    voiceChordCaptured = false;

                    int sequence = Interlocked.Increment(ref voiceSequence);
                    BeginFinishVoiceSession(sequence, keepDraft);
                }

                return (IntPtr)1;
            }
        }

        return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);
    }

    private static bool IsCtrlKey(uint vkCode)
    {
        return vkCode == VK_CONTROL || vkCode == VK_LCONTROL || vkCode == VK_RCONTROL;
    }

    private static bool IsAltKey(uint vkCode)
    {
        return vkCode == VK_MENU || vkCode == VK_LMENU || vkCode == VK_RMENU;
    }

'''
    s = s[:start] + new_callback + s[end:]

    old_reset = '''    private void ResetVoiceState()
    {
        Interlocked.Increment(ref voiceSequence);
        voiceHeld = false;
        voiceKeepDraft = false;
        middleButtonCaptured = false;
    }
'''
    new_reset = '''    private void ResetVoiceState()
    {
        Interlocked.Increment(ref voiceSequence);
        voiceHeld = false;
        voiceKeepDraft = false;
        middleButtonCaptured = false;
        voiceCtrlDown = false;
        voiceAltDown = false;
        voiceZeroDown = false;
        voiceChordCaptured = false;
    }
'''
    if old_reset not in s:
        raise RuntimeError("v1.4 ResetVoiceState block not found")
    s = s.replace(old_reset, new_reset)

    old_helper = '''    private static void SendRightCtrlTap()
    {
        keybd_event(VK_RCONTROL, 0, KEYEVENTF_EXTENDEDKEY, SyntheticExtraInfo);
        keybd_event(VK_RCONTROL, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, SyntheticExtraInfo);
    }
'''
    new_helper = '''    private static void SendCtrlAlt0TapAndNeutralize()
    {
        // 先释放可能已经送进系统的实体修饰键状态。左右两侧都发 Up，避免 Options+
        // 对 Ctrl / Alt 的左右实现差异影响滚轮或点击。多余的 KeyUp 对系统无害。
        keybd_event(VK_LCONTROL, 0, KEYEVENTF_KEYUP, SyntheticExtraInfo);
        keybd_event(VK_RCONTROL, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, SyntheticExtraInfo);
        keybd_event(VK_LMENU, 0, KEYEVENTF_KEYUP, SyntheticExtraInfo);
        keybd_event(VK_RMENU, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, SyntheticExtraInfo);

        // 再用固定的左 Ctrl + 左 Alt + 0 补发一个极短快捷键点按。微信只需要收到一次
        // Ctrl+Alt+0 来进入长语音模式，不需要在整段讲话期间保持修饰键按下。
        keybd_event(VK_LCONTROL, 0, 0, SyntheticExtraInfo);
        keybd_event(VK_LMENU, 0, 0, SyntheticExtraInfo);
        keybd_event(VK_0, 0, 0, SyntheticExtraInfo);
        keybd_event(VK_0, 0, KEYEVENTF_KEYUP, SyntheticExtraInfo);
        keybd_event(VK_LMENU, 0, KEYEVENTF_KEYUP, SyntheticExtraInfo);
        keybd_event(VK_LCONTROL, 0, KEYEVENTF_KEYUP, SyntheticExtraInfo);
    }
'''
    if old_helper not in s:
        raise RuntimeError("v1.4 SendRightCtrlTap block not found")
    s = s.replace(old_helper, new_helper)

s = s.replace("无法安装键盘监听。\\r\\n\\r\\n右 Ctrl 语音功能无法工作。", "无法安装键盘监听。\\r\\n\\r\\nCtrl+Alt+0 语音功能无法工作。")
s = s.replace('voiceEnabledItem = new ToolStripMenuItem("启用右 Ctrl 长按语音发送");', 'voiceEnabledItem = new ToolStripMenuItem("启用 Ctrl+Alt+0 长按语音发送");')
s = s.replace('"连续退格保持原样；右 Ctrl 物理键可按住说话，松开自动结束并发送。按住期间点一下滚轮中键可保留文字不发送。",', '"连续退格保持原样；MX 语音键请映射为 Ctrl+Alt+0。按住说话，松开自动结束并发送；按住期间点一下滚轮中键可保留文字不发送。",')
s = s.replace('trayIcon.Text = "MXBackspaceHold v1.4";', 'trayIcon.Text = "MXBackspaceHold v1.4.1";')
s = s.replace('"MXBackspaceHold v1.4 已启动",', '"MXBackspaceHold v1.4.1 已启动",')
program.write_text(s, encoding="utf-8-sig")

r = readme.read_text(encoding="utf-8")
r = r.replace("当前 v1.4 同时提供：", "当前 v1.4.1 同时提供：")
r = r.replace("- **右 Ctrl 长按语音**：将 MX Master 4 的一个实体键在 Logi Options+ 中直接映射为 **Right Ctrl**；按住开始微信输入法长语音，松开自动结束并发送。", "- **Ctrl+Alt+0 长按语音**：将 MX Master 4 的一个实体键在 Logi Options+ 中映射为 **Ctrl+Alt+0**；按住开始微信输入法长语音，松开自动结束并发送。")
r = r.replace("- **滚轮不受 Ctrl 影响**：实体 Right Ctrl 会被程序吞掉，仅在开始时补发一次短促的 Right Ctrl 点按，因此讲话时可以正常滚动页面。", "- **滚轮不受修饰键影响**：识别到 Ctrl+Alt+0 后，程序立即中和 Ctrl/Alt，并只补发一次极短的 Ctrl+Alt+0 点按；讲话期间可以正常滚动页面。")
r = r.replace("`Keyboard Shortcut -> Right Ctrl`", "`Keyboard Shortcut -> Ctrl+Alt+0`")
r = r.replace("不要使用 Smart Action 来模拟长按。", "直接使用普通 Keyboard Shortcut 映射；不要用 Smart Action 来模拟按住/松开。")
readme.write_text(r, encoding="utf-8")

u = usage.read_text(encoding="utf-8-sig")
u = u.replace("MXBackspaceHold v1.4 - MX Master 4 连续退格 + 右 Ctrl 长按语音", "MXBackspaceHold v1.4.1 - MX Master 4 连续退格 + Ctrl+Alt+0 长按语音")
u = u.replace("v1.4 在 v1.3 连续退格功能完全保留的基础上", "v1.4.1 在 v1.3 连续退格功能完全保留的基础上")
u = u.replace("    Keyboard Shortcut -> 右 Ctrl（Right Ctrl）", "    Keyboard Shortcut -> Ctrl + Alt + 0")
u = u.replace("不要让 Smart Action 自己录 Ctrl Down / Ctrl Up。", "不要让 Smart Action 自己录按键序列；使用普通 Keyboard Shortcut 映射。")
u = u.replace("原因：本程序需要看到“实体右 Ctrl 的按下”和“实体右 Ctrl 的松开”，才能知道你实际按住了多久。", "原因：本程序把 Ctrl+Alt+0 当成 MX 语音键的专用触发组合，并根据这个组合的按下/松开判断你实际按住了多久。")
u = u.replace("程序会吞掉 MX Master 4 发出的实体右 Ctrl。\n\n按下时，它只向系统补发一次很短的“右 Ctrl 点按”，用来启动微信输入法的长语音模式；随后即使你手指一直按着实体鼠标键，Windows 也不会一直处于 Ctrl 按下状态。", "程序识别到 MX Master 4 发出的 Ctrl+Alt+0 后，会立即中和系统里的 Ctrl/Alt 状态，再补发一次极短的 Ctrl+Alt+0 点按，用来启动微信输入法长语音。随后即使你手指一直按着实体鼠标键，Windows 也不会一直处于 Ctrl/Alt 按下状态。")
u = u.replace("2. 按住 MX Master 4 的语音键（Options+ 里映射为右 Ctrl）。", "2. 按住 MX Master 4 的语音键（Options+ 里映射为 Ctrl+Alt+0）。")
u = u.replace("4. 确认“启用右 Ctrl 长按语音发送”已勾选。", "4. 确认“启用 Ctrl+Alt+0 长按语音发送”已勾选。")
u = u.replace("- 本程序会接管右 Ctrl。启用语音功能时，实体键盘上的“右 Ctrl”也会被视为语音键。\n- 左 Ctrl 不受影响。\n- 如果暂时需要正常使用右 Ctrl，可在托盘里取消“启用右 Ctrl 长按语音发送”。", "- 本程序只在识别到完整的 Ctrl+Alt+0 组合后接管该组合；单独 Ctrl、Alt 和普通 Ctrl 快捷键不受影响。\n- 实体键盘如果手动按 Ctrl+Alt+0，也会触发同样的语音流程。\n- 如果暂时需要正常使用 Ctrl+Alt+0，可在托盘里取消“启用 Ctrl+Alt+0 长按语音发送”。")
u = u.replace("v1.4 新增：\n- 全局低级键盘 Hook，监听右 Ctrl 的按下/松开。\n- 实体右 Ctrl 被吞掉，避免按住讲话时 Ctrl+滚轮缩放页面。\n- Right Ctrl Down 时补发一次短促右 Ctrl 点按，启动微信长语音。\n- Right Ctrl Up 时自动 Enter 结束语音，延迟后再 Enter 发送。", "v1.4.1 语音触发调整：\n- 全局低级键盘 Hook 改为识别 Ctrl+Alt+0 组合。\n- 完整组合被识别后立即中和 Ctrl/Alt，避免按住讲话时 Ctrl+滚轮缩放页面或 Alt 影响前台。\n- 组合按下时补发一次短促 Ctrl+Alt+0 点按，启动微信长语音。\n- 组合全部松开时自动 Enter 结束语音，延迟后再 Enter 发送。")
usage.write_text(u, encoding="utf-8-sig")

print("v1.4.1 Ctrl+Alt+0 migration applied")
