# WeTypeAudioGuard

Windows 托盘小工具：阻止微信输入法语音识别时强制静音后台音频，或按用户设置临时降低后台音量。

## 功能

- 后台托盘运行，无常驻窗口。
- 自动识别 `wetype_*` 录音会话进入 Active 状态。
- 对所有默认播放端点中的音频 Session 生效，不绑定网易云、Chrome、Spotify 等特定应用。
- 模式：保持原音量（100%）、20%、30%、50%、自定义 1%~100%。
- 以“原应用音量 × 百分比”计算临时音量，保持各应用之间的相对比例。
- 平时持续保存语音开始前的会话基线，避免微信输入法“录音 Active 与 Mute 同时发生”导致抓不到原状态。
- 语音结束后恢复每个 Session 原始 Volume / Mute 状态。
- 原本就是静音的 Session 不会被强行打开。
- 支持开机启动。
- `settings.json` 和 `WeTypeAudioGuard.log` 都保存在 EXE 所在文件夹。

## 原理

微信输入法开始语音时会将正在播放的 Windows Core Audio Session 设为 Mute。该程序监听 `wetype_*` Capture Session，语音期间持续保护 Render Session：取消由微信输入法造成的 Mute，并按用户设置维持目标音量。

## 构建

```powershell
dotnet publish .\WeTypeAudioGuard.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```
