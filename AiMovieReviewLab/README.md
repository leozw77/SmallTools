# AI 观影短评实验台 v0.1-preview.2

独立 Prompt Lab，用于在正式接入“观影助手”前测试 AI 三轮采访、豆瓣事实定位和 Prompt 迭代。

## preview.2 重点

### 1. 豆瓣链接改为必填锚点

不再要求用户手填电影名。输入必须是：

```text
https://movie.douban.com/subject/<数字ID>/
```

第一轮成功后，影片名由事实定位结果自动写入只读框。

### 2. 第一轮内部强制事实定位，不增加额外用户回合

Qwen / 百炼使用 OpenAI-compatible Responses API：

```text
同一次第1轮 API
→ web_extractor 强制读取指定豆瓣 URL
→ 必要时 web_search 补具体场景/人物/台词事实
→ 输出 factLocalization
→ 直接输出第1轮3个主观采访问题
```

程序会检查 `web_extractor` 是否真的访问了指定 `/subject/<id>/`；没有访问就停止第一轮，避免在错误电影事实上继续采访。

第二、三轮不重新开放泛搜索，只使用第一轮已验证事实和用户回答。

### 3. 禁止让用户替 AI 回答电影事实

Prompt 明确禁止把以下内容作为正常采访题：

- “这句话是谁对谁说的？”
- “当时发生了什么？”
- “这个人物是不是已经知道自己不行了？”

人物死亡、心理、动作、镜头等客观前提如果没有验证，不能塞进问题或 A/B/C 选项。

### 4. 实体只允许“验证后锁定”

实体需要同时满足：

- `status = verified`
- `confidence = high`
- `evidence` 非空

才能进入后续锁定实体。低置信度、语音近音但没有证据的名字只能留在 `uncertainEntities`，不能自动把不同角色合并。

### 5. 完整日志

新增：

- **导出完整日志到桌面**：直接生成 Markdown；
- **复制完整日志**：完整内容进入剪贴板。

日志包含：

- Provider / Base URL / Model / Thinking / 价格；
- 豆瓣链接 / Subject ID / 识别影片 / 评分 / 初始评论；
- 字幕信息与清洗后的完整字幕；
- 第一轮事实定位、来源、工具调用、已验证/未确认实体与事实；
- 每轮事实快照；
- 三轮每道问题、A/B/C/D 全部选项、用户勾选和自由补充；
- 最终自由发言与最终短评；
- 当前采访 Prompt 与短评 Prompt 全文；
- 每次 API 的 Token、缓存 Token、Reasoning Token、首 Token、总耗时、估算费用；
- web_search / web_extractor 调用次数；
- 模型 Content、Request JSON、Raw Response；
- Provider 显式返回的 reasoning summary（若存在）。

**API Key 永不写入日志。**

### 6. UI 只修可用性，不重设计

- 顶部输入区改为可滚动/自动撑开；
- 操作按钮允许换行；
- 三轮问题卡根据窗口宽度调整；
- 修复 125% / 150% DPI 下容易遮挡、裁切的问题；
- 增加“第一轮事实定位 / 豆瓣读取”窗口。

## 继续保留的能力

- Qwen / DeepSeek / GLM / Custom OpenAI-compatible Provider 切换；
- Prompt 编辑、导入、导出、历史备份；
- SRT / ASS / SSA 字幕清洗；
- 3 + 3 + 3 三轮采访；
- 程序固定渲染 A/B/C 多选 + D 都不符合 + 自由补充；
- 第三轮后固定自由发言；
- 最终短评 ≤330 字；
- Token / 耗时 / 原始请求响应调试。

## Qwen 默认配置

```text
Base URL: https://dashscope.aliyuncs.com/compatible-mode/v1
Model: qwen3.7-flash
Thinking: OFF
```

第一轮会从同一个 Base URL 调用 `/responses`；后续采访和最终短评使用 `/chat/completions`。

## 构建

```powershell
dotnet restore .\AiMovieReviewLab.csproj
dotnet build .\AiMovieReviewLab.csproj -c Release
```

GitHub Actions 发布为 framework-dependent .NET 8 win-x64 包，需要 Windows 已安装 .NET 8 Desktop Runtime。
