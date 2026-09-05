# MXKeyboardDiag

一个只读诊断工具，用来抓取 MX Master 4 / Logi Options+ 触发时 Windows 实际收到的输入事件。

## 记录内容

- 低级键盘 Hook：`WM_KEYDOWN / WM_KEYUP / WM_SYSKEYDOWN / WM_SYSKEYUP`
- `vkCode`
- `scanCode`
- `flags`
- `dwExtraInfo`
- 是否为 injected 事件
- Raw Input 键盘事件
- Raw Input 鼠标按钮事件
- `GetAsyncKeyState` 对 Ctrl / Alt / 0 / VK_NONAME(252) 的状态变化
- 当前前台窗口标题

## 测试方法

1. 先退出 `MXBackspaceHold`，避免正式程序自己的 Hook 干扰诊断。
2. 保持微信输入法里 `Ctrl+Alt+0` 的语音快捷键配置不变。
3. 运行 `MXKeyboardDiag.exe`。
4. 切回一个可输入文字的窗口。
5. 按住 MX Master 4 的语音侧键约 2 秒。
6. 松开侧键。
7. 回到诊断程序，点击“复制全部”，或者打开桌面自动生成的 `MXKeyboardDiag-*.log`。
8. 把日志发回分析。

## 说明

本工具不会拦截、修改或模拟任何键盘/鼠标输入，只记录事件。
