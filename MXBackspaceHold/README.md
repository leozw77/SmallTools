# MXBackspaceHold

MX Master 4 的 Windows 小工具。

当前 v1.4 同时提供：

- **连续退格**：把 XButton1/XButton2 变成键盘式 Backspace，支持长按连续删除。
- **右 Ctrl 长按语音**：将 MX Master 4 的一个实体键在 Logi Options+ 中直接映射为 **Right Ctrl**；按住开始微信输入法长语音，松开自动结束并发送。
- **保留草稿手势**：按住语音键期间点击一次滚轮中键，松开时只结束语音，不自动发送。
- **滚轮不受 Ctrl 影响**：实体 Right Ctrl 会被程序吞掉，仅在开始时补发一次短促的 Right Ctrl 点按，因此讲话时可以正常滚动页面。

## 使用前配置

在 **Logi Options+** 中，将语音实体键直接设置为：

`Keyboard Shortcut -> Right Ctrl`

不要使用 Smart Action 来模拟长按。

详见 `使用说明.txt`。

## 基线

`baseline/MXBackspaceHold_Windows_v1.3.zip` 是本次开发收到的原始 v1.3 包，作为只读基线保留。
