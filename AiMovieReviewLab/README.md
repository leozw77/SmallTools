# AI 观影短评实验台 v0.1-preview.1

独立 Prompt Lab，用于在正式接入“观影助手”前稳定测试三轮 AI 采访流程。

## 本版目标

- 复用旧 `AiMovieReview_TestHarness v0.5-preview.7` 的字幕清洗、Prompt 编辑、结构化问答、Token/耗时调试思路。
- 不再写死 DeepSeek；支持 Provider / Base URL / Model / API Key 切换。
- 内置 Qwen / 百炼、DeepSeek、GLM / 智谱、Custom OpenAI-compatible 预设。
- 从“一次生成10题”改为三轮采访：`3 + 3 + 3 + 最终自由发言`。
- 每题模型只返回 `question + 3 options`；程序固定渲染 A/B/C 多选、D“都不符合”、自由补充，避免模型漏选项。
- 用户自由补充权重高于选项；最后自由发言属于最高权重。
- 支持字幕 SRT / ASS / SSA 清洗；有字幕时作为高权重剧情事实依据，无字幕时可使用模型供应商联网搜索。
- 支持人物/实体别名归一，重点观察语音输入的“老扎/老张/老沙/老三”类漂移。
- Thinking 默认关闭。
- 显示输入 / 输出 / 缓存 / reasoning Token、首 Token 时间、总耗时和按当前配置价格估算的人民币费用。
- 保存/载入测试案例，方便同一电影同一句初评反复 A/B 测试 Prompt。
- Prompt 可直接编辑、导入导出；每次覆盖自定义 Prompt 时旧版本自动进入 LocalAppData History。

## Provider 说明

### Qwen / 百炼

默认：

- Base URL: `https://dashscope.aliyuncs.com/compatible-mode/v1`
- Model: `qwen3.7-flash`
- 联网搜索：支持
- Thinking：支持，默认关闭
- 默认估价（输入 <= 32K）：输入 ¥0.2/M、输出 ¥0.8/M、缓存输入 ¥0.04/M

Base URL、模型名、价格都可直接编辑，不写死。

### DeepSeek

默认：

- Base URL: `https://api.deepseek.com`
- Model: `deepseek-v4-flash`
- Thinking：支持，默认关闭
- 原生 Chat Completions 预设不声明内置联网搜索

### GLM / 智谱

默认：

- Base URL: `https://open.bigmodel.cn/api/paas/v4`
- Model: `glm-4.7-flash`
- 联网搜索：使用 `web_search` 工具
- Thinking：支持，默认关闭
- 价格默认 0，便于测试当前免费 Flash；若价格变化可在界面直接修改

## 三轮采访

第一轮：发现观点——感受来源、真正焦点、初始记忆点与整体评分的权重。

第二轮：取得材料——例子、原因、变化/影响/比较；禁止重复已获得的信息。

第三轮：收束观点——权衡、评分/总体判断、余味；原则上不再开新主题。

第三轮后由程序固定显示：

> 还有没有什么刚才没问到，但你特别想说的？

## 构建

Windows + .NET 8 Desktop Runtime 运行发布包。

源码构建：

```powershell
dotnet restore .\AiMovieReviewLab.csproj
dotnet build .\AiMovieReviewLab.csproj -c Release
```
