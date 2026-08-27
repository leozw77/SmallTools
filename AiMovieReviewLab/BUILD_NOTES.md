# v0.1-preview.1 开发说明

基线：`AiMovieReview_TestHarness_v0.5-preview.7_DynamicExpression_Source_2026-08-27.zip`

主要复用：

- `SubtitleCleaner`：直接迁移并改命名空间。
- Prompt Store / Prompt Editor：保留“默认 + 自定义 + 导入/导出”，增加历史版本自动备份。
- 结构化题目 UI：保留 WinForms 动态生成思路；改成程序固定 A/B/C/D + 自由补充。
- API 调试：沿用 SSE / Token / 首 Token / 总耗时思想，重构为多 Provider OpenAI-compatible 客户端。

主要废弃：

- DeepSeek endpoint/model 写死。
- 一次生成10题。
- 9题 + Q10 free 的旧结构。
- 把豆瓣短评/长评/讨论作为默认 discovery 观点材料。

本版本优先验证产品体验，不直接接入正式观影助手。
