from pathlib import Path

ROOT = Path(__file__).resolve().parent
program = ROOT / "Program.cs"
readme = ROOT / "README.md"
usage = ROOT / "使用说明.txt"

s = program.read_text(encoding="utf-8-sig")

# v1.4.2: stop trying to reconstruct/re-inject Ctrl+Alt+0.  Diagnostics proved
# WeType keeps the modifier held until the MX button is physically released and
# emits VK_NONAME (0xFC) with extraInfo 0x57545950.  We only observe that native
# lifecycle and send ONE Enter after release, because release itself already ends
# WeChat long voice.

s = s.replace(
    "    private const byte VK_RMENU     = 0xA5;\n",
    "    private const byte VK_RMENU     = 0xA5;\n    private const byte VK_NONAME   = 0xFC;\n"
)

s = s.replace(
    "    private const long SyntheticExtraInfoValue = 0x4D584248; // \"MXBH\"\n",
    "    private const long SyntheticExtraInfoValue = 0x4D584248; // \"MXBH\"\n"
    "    private const long WeTypeExtraInfoValue = 0x57545950;    // observed WeType marker\n"
)

old_state = '''    // 语音功能：MX Master 4 的目标实体键在 Options+ 里映射为 Ctrl+Alt+0。
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
new_state = '''    // 语音功能：MX Master 4 的目标实体键在 Options+ 里映射为 Alt+0。
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
'''
if old_state not in s:
    raise RuntimeError("v1.4.1 voice state block not found")
s = s.replace(old_state, new_state)

start = s.index("    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)")
end = s.index("    private void BeginFinishVoiceSession", start)
new_callback = r'''    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
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
                    voiceAltDown = true;
                }
                else if (isUp)
                {
                    voiceAltDown = false;

                    // Alt+0 的 Alt Up 就是 Options+ 实体语音键真正松开的时刻。
                    // 不吞这个 Up：先让微信输入法原生结束语音，再由我们延迟一次 Enter 发送。
                    if (voiceHeld)
                    {
                        bool keepDraft = voiceKeepDraft;
                        voiceHeld = false;
                        voiceKeepDraft = false;

                        int sequence = Interlocked.Increment(ref voiceSequence);
                        BeginFinishVoiceSession(sequence, keepDraft);
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
            if (!voiceHeld && isDown && voiceAltDown && isVoiceTrigger)
            {
                Interlocked.Increment(ref voiceSequence);
                voiceHeld = true;
                voiceKeepDraft = false;
                middleButtonCaptured = false;
                UpdateVoiceStatus("语音：按住说话中");
            }
        }

        return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);
    }

    private static bool IsAltKey(uint vkCode)
    {
        return vkCode == VK_MENU || vkCode == VK_LMENU || vkCode == VK_RMENU;
    }

'''
s = s[:start] + new_callback + s[end:]

old_finish = '''    private void BeginFinishVoiceSession(int sequence, bool keepDraft)
    {
        UpdateVoiceStatus(keepDraft ? "语音：结束后保留文字" : "语音：结束并等待自动发送");

        ThreadPool.QueueUserWorkItem(delegate
        {
            // 这里的 35 ms 不是“等微信排版”，只是把结束动作放到实体键松开回调之后再执行。
            Thread.Sleep(35);

            if (sequence != Volatile.Read(ref voiceSequence) || !voiceEnabled)
                return;

            // 第一个 Enter：结束微信长语音并让它完成最后的转写/排版。
            SendEnter();

            if (keepDraft)
            {
                UpdateVoiceStatus("语音：已保留文字，待机");
                return;
            }

            // 不监控微信、不监控输入框。只使用一个可调的简单延迟。
            Thread.Sleep(voiceSendDelayMs);

            if (sequence != Volatile.Read(ref voiceSequence) || !voiceEnabled)
                return;

            // 第二个 Enter：发送当前输入框内容。
            SendEnter();
            UpdateVoiceStatus("语音：已发送，待机");
        });
    }
'''
new_finish = '''    private void BeginFinishVoiceSession(int sequence, bool keepDraft)
    {
        UpdateVoiceStatus(keepDraft ? "语音：已松开，保留文字" : "语音：已松开，等待自动发送");

        ThreadPool.QueueUserWorkItem(delegate
        {
            // Alt Up 已经被原样放给微信输入法，因此微信会自己结束长语音并做最后转写。
            // 保留草稿时不需要再发任何 Enter。
            if (keepDraft)
            {
                UpdateVoiceStatus("语音：已保留文字，待机");
                return;
            }

            // 只等一次排版/落字延迟，然后发一个 Enter 发送消息。
            Thread.Sleep(voiceSendDelayMs);

            if (sequence != Volatile.Read(ref voiceSequence) || !voiceEnabled)
                return;

            SendEnter();
            UpdateVoiceStatus("语音：已发送，待机");
        });
    }
'''
if old_finish not in s:
    raise RuntimeError("v1.4.1 finish block not found")
s = s.replace(old_finish, new_finish)

old_reset = '''    private void ResetVoiceState()
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
new_reset = '''    private void ResetVoiceState()
    {
        Interlocked.Increment(ref voiceSequence);
        voiceHeld = false;
        voiceKeepDraft = false;
        middleButtonCaptured = false;
        voiceAltDown = false;
    }
'''
if old_reset not in s:
    raise RuntimeError("v1.4.1 ResetVoiceState block not found")
s = s.replace(old_reset, new_reset)

helper_start = s.find("    private static void SendCtrlAlt0TapAndNeutralize()")
if helper_start >= 0:
    helper_end = s.index("    private void SetRepeatInterval", helper_start)
    s = s[:helper_start] + s[helper_end:]

s = s.replace("Ctrl+Alt+0 语音功能无法工作。", "Alt+0 语音功能无法工作。")
s = s.replace('voiceEnabledItem = new ToolStripMenuItem("启用 Ctrl+Alt+0 长按语音发送");', 'voiceEnabledItem = new ToolStripMenuItem("启用 Alt+0 长按语音自动发送");')
s = s.replace('trayIcon.Text = "MXBackspaceHold v1.4.1";', 'trayIcon.Text = "MXBackspaceHold v1.4.2";')
s = s.replace('"MXBackspaceHold v1.4.1 已启动",', '"MXBackspaceHold v1.4.2 已启动",')
s = s.replace(
    '"连续退格保持原样；MX 语音键请映射为 Ctrl+Alt+0。按住说话，松开自动结束并发送；按住期间点一下滚轮中键可保留文字不发送。",',
    '"连续退格保持原样；MX 语音键请映射为 Alt+0。微信原生处理按住/松开，本程序仅在松开后自动发送；中键可保留文字。",'
)
program.write_text(s, encoding="utf-8-sig")

readme.write_text('''# MXBackspaceHold

MX Master 4 的 Windows 小工具。

当前 v1.4.2 同时提供：

- **连续退格**：保留 v1.3 的 XButton1/XButton2 长按连续删除。
- **Alt+0 长按语音自动发送**：在 Logi Options+ 和微信输入法中都把语音键设为 **Alt+0**。按住/松开完全交给微信输入法原生处理；程序只旁观释放时刻并自动发送。
- **保留草稿手势**：按住语音键期间点击一次滚轮中键，本轮松开后不自动发送。
- **不再碰 Ctrl**：避免浏览器中 Ctrl+滚轮触发网页缩放。

## 为什么 v1.4.1 没成功

诊断日志证明 Options+/微信输入法的 Ctrl+Alt+0 并不会以普通 `0` 键出现在低级键盘 Hook：实际看到的是 Ctrl/Alt 按住状态，以及微信输入法产生的 `VK_NONAME(0xFC)` 标记。v1.4.1 却死等标准 `VK_0`，所以程序没有真正进入语音状态，松手后自然不会执行自动发送。

同时 v1.4.1 还尝试“中和 Ctrl/Alt 再重放快捷键”，这会和微信已经正常工作的原生按住/松开生命周期重复处理。v1.4.2 删除这层干预。

## v1.4.2 逻辑

1. Options+ 原生发 Alt+0；微信输入法自己开始语音。
2. 程序观察到 Alt + 微信输入法标记后，仅记录“语音进行中”，不拦截任何键。
3. 实体键松开时 Alt Up 原样交给微信，微信自己结束语音并完成最终转写。
4. 程序等待可调延迟（默认 800 ms），只发 **一次 Enter** 发送消息。
5. 如果语音期间点过滚轮中键，则松开后 **不发 Enter**，保留文字。

## 使用前配置

- Logi Options+：语音实体键设为 `Keyboard Shortcut -> Alt+0`
- 微信输入法：长语音快捷键也设为 `Alt+0`
- 不要使用 Smart Action。

`baseline/MXBackspaceHold_Windows_v1.3.zip` 为只读基线。
''', encoding="utf-8")

usage.write_text('''MXBackspaceHold v1.4.2 - MX Master 4 连续退格 + Alt+0 长按语音自动发送

【必须配置】
1. Logi Options+：把语音实体键设为 Alt + 0（普通 Keyboard Shortcut，不要 Smart Action）。
2. 微信输入法：长语音快捷键也设为 Alt + 0。

【正常语音】
- 按住 MX 语音键：微信输入法原生开始长语音。
- 一直按着：继续说话。
- 松开：微信输入法原生结束语音并完成最终转写。
- 程序等待“语音自动发送延迟”（默认 800 ms），然后只发一次 Enter 发送消息。

注意：v1.4.2 不再模拟或重放 Alt+0，也不拦截 Alt 的按下/松开。

【这次不想自动发送】
按住语音键期间点一下滚轮中键。
程序会标记本轮“保留文字”；松开后微信仍正常结束语音，但程序不会发 Enter。

【为什么改成 Alt+0】
之前 Ctrl+Alt+0 在网页里会因为 Ctrl 一直按住而把滚轮变成 Ctrl+滚轮，从而缩放页面。Alt+0 不使用 Ctrl，避免这一副作用。

【为什么 v1.4.1 没有自动发送】
诊断工具实际抓到：
- Ctrl / Alt 的 Down 和 Up 都存在；
- 微信输入法把触发主键表现为 VK_NONAME(0xFC)，extraInfo=0x57545950；
- 并没有出现 v1.4.1 代码所等待的标准 VK_0。

因此 v1.4.1 虽然微信本身能正常语音，但程序没有进入 voiceHeld 状态，所以松手不会走自动发送逻辑。

v1.4.2 不再等待标准 0：看到 Alt 按下并出现微信输入法标记（同时兼容标准 VK_0）就记录语音开始；真正 Alt Up 时记录松手。由于 Alt Up 本身已经让微信结束语音，所以本版只需要延迟后发一个 Enter，而不是旧版的两个 Enter。

【连续退格】
v1.3 功能保持不变：
- 点一下删除 1 个字符；
- 按住连续删除；
- XButton1 / XButton2 可切换；
- 重复速度、长按延迟、开机启动、最近检测到侧键均保留。

【托盘设置】
- 启用连续退格
- XButton1 / XButton2
- 重复速度
- 长按延迟
- 启用 Alt+0 长按语音自动发送
- 语音自动发送延迟：500 / 800 / 1000 / 1500 / 2000 ms
- 开机自动启动

如果松开后文字还没完全落完就发送，把延迟从 800 ms 调到 1000 或 1500 ms；如果感觉慢，可以试 500 ms。
''', encoding="utf-8-sig")

print("v1.4.2 Alt+0 migration applied")
