from pathlib import Path

ROOT = Path(__file__).resolve().parent
program = ROOT / "Program.cs"
readme = ROOT / "README.md"
usage = ROOT / "使用说明.txt"

s = program.read_text(encoding="utf-8-sig")

if "HapticEndpoint" not in s:
    s = s.replace(
        "using System.Runtime.InteropServices;\n",
        "using System.Runtime.InteropServices;\nusing System.Net;\n"
    )

    marker = '    private const string RunValueName = "MXBackspaceHold";\n'
    insert = marker + '\n    // HapticWebPlugin：只使用一个轻微波形，作为语音开始/自动发送两个状态反馈。\n    private const string HapticEndpoint = "https://local.jmw.nz:41443/haptic/subtle_collision";\n'
    if marker not in s:
        raise RuntimeError("RunValueName marker not found")
    s = s.replace(marker, insert, 1)

    start_marker = '                UpdateVoiceStatus("语音：按住说话中");\n'
    if start_marker not in s:
        raise RuntimeError("voice start marker not found")
    s = s.replace(
        start_marker,
        start_marker + '                TryHaptic();\n',
        1
    )

    send_marker = '            SendEnter();\n            UpdateVoiceStatus("语音：已发送，待机");\n'
    if send_marker not in s:
        raise RuntimeError("voice send marker not found")
    s = s.replace(
        send_marker,
        '            SendEnter();\n            TryHaptic();\n            UpdateVoiceStatus("语音：已发送，待机");\n',
        1
    )

    helper_marker = '    private void SetRepeatInterval(int intervalMs)\n'
    if helper_marker not in s:
        raise RuntimeError("SetRepeatInterval marker not found")
    helper = r'''    private static void TryHaptic()
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

'''
    s = s.replace(helper_marker, helper + helper_marker, 1)

s = s.replace('trayIcon.Text = "MXBackspaceHold v1.4.2";', 'trayIcon.Text = "MXBackspaceHold v1.4.3";')
s = s.replace('"MXBackspaceHold v1.4.2 已启动",', '"MXBackspaceHold v1.4.3 已启动",')
s = s.replace(
    '"连续退格保持原样；MX 语音键请映射为 Alt+0。微信原生处理按住/松开，本程序仅在松开后自动发送；中键可保留文字。",',
    '"连续退格保持原样；MX 语音键请映射为 Alt+0。语音开始和自动发送各震一下 subtle_collision；中键保留文字时不会触发发送震动。",'
)
program.write_text(s, encoding="utf-8-sig")

r = readme.read_text(encoding="utf-8")
r = r.replace("当前 v1.4.2 同时提供：", "当前 v1.4.3 同时提供：")
if "subtle_collision" not in r:
    needle = "- **保留草稿手势**：按住语音键期间点击一次滚轮中键，松开时只结束语音，不自动发送。\n"
    if needle in r:
        r = r.replace(
            needle,
            needle + "- **极简触觉反馈**：语音真正开始时震一下；程序自动发送时再震一下。两次都固定使用 `subtle_collision`，不增加更多震动状态。\n"
        )
readme.write_text(r, encoding="utf-8")

u = usage.read_text(encoding="utf-8-sig")
u = u.replace("MXBackspaceHold v1.4.2", "MXBackspaceHold v1.4.3")
if "【v1.4.3 触觉反馈】" not in u:
    u += '''\n\n【v1.4.3 触觉反馈】\n- 需要本机 HapticWebPlugin 已正常运行。\n- 固定只使用 subtle_collision 一个波形。\n- 微信语音真正开始时震一下。\n- 自动发送 Enter 发出时再震一下。\n- 中键选择“保留草稿”时不会出现第二次发送震动。\n- 震动调用完全异步；插件不可用时静默忽略，不影响语音、发送和连续退格。\n'''
usage.write_text(u, encoding="utf-8-sig")
