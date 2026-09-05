# MXBackspaceHold

MX Master 4 的 Windows 小工具。

当前 v1.4.1 同时提供：

- **连续退格**：把 XButton1/XButton2 变成键盘式 Backspace，支持长按连续删除。
- **Ctrl+Alt+0 长按语音**：将 MX Master 4 的一个实体键在 Logi Options+ 中映射为 **Ctrl+Alt+0**；按住开始微信输入法长语音，松开自动结束并发送。
- **保留草稿手势**：按住语音键期间点击一次滚轮中键，松开时只结束语音，不自动发送。
- **滚轮不受修饰键影响**：识别到 Ctrl+Alt+0 后，程序立即中和 Ctrl/Alt，并只补发一次极短的 Ctrl+Alt+0 点按；讲话期间可以正常滚动页面。

## 使用前配置

在 **Logi Options+** 中，将语音实体键直接设置为：

`Keyboard Shortcut -> Ctrl+Alt+0`

直接使用普通 Keyboard Shortcut 映射；不要用 Smart Action 来模拟按住/松开。

详见 `使用说明.txt`。

## 基线

`baseline/MXBackspaceHold_Windows_v1.3.zip` 是本次开发收到的原始 v1.3 包，作为只读基线保留。
