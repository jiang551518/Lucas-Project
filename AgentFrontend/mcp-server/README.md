# Lucas Agent MCP 使用说明

Lucas Agent MCP 是一个运行在本机的 stdio 桥接器，让 Codex 可以调用 Lucas Agent 中配置的模型和 MiniMax H3。它不需要独立的 MCP 服务端口。提交视频会创建真实 H3 任务并占用生成端算力。

## 使用前准备

1. 启动 Lucas Agent。
2. 如需用 Codex 写视频提示词，在 Lucas Agent 中配置可用的 DeepSeek 服务商。
3. 如需生成视频，在 Lucas Agent 的 AI 视频设置中配置本机 ComfyUI 或远程 Comfy API Proxy，并确认 H3 服务在线。
4. 安装 Node.js/npm，并确保命令行可以运行 `node` 和 `codex`。

本机 H3 使用 Lucas Agent 当前选中的 ComfyUI 配置；远程 H3 使用 Lucas Agent 中保存的远程 API 地址和加密保存的 Token。MCP 不读取或要求用户粘贴 API Key/Token。

## 注册到 Codex

在 PowerShell 执行：

```powershell
Set-Location "D:\Lucas Project\AgentFrontend"
npm install
codex mcp add lucas-agent -- node "D:\Lucas Project\AgentFrontend\mcp-server\index.mjs"
codex mcp list
```

重启 Codex 以加载新工具。可用 `codex mcp get lucas-agent` 查看配置；如需重建注册项，先运行 `codex mcp remove lucas-agent`，再执行上面的 `codex mcp add`。

打包版 Lucas Agent 会将动态后端地址写入 `%LOCALAPPDATA%\LucasAgent\backend-endpoint.txt`，MCP 会自动读取。开发模式会回退到 `http://127.0.0.1:5008`。也可通过环境变量 `LUCAS_AGENT_URL` 指定本机后端地址。

## MCP 工具

| 工具 | 参数与用途 |
| --- | --- |
| `lucas_status` | 无参数。检查 Lucas Agent 后端、已配置模型及 H3 连接状态。建议每次操作先调用。 |
| `lucas_list_sessions` | 无参数。列出本地 Lucas Agent 会话。 |
| `lucas_run_agent` | `prompt` 必填；`mode` 为 `daily` 或 `video`。调用 Lucas Agent 已配置模型。`video` 模式要求已配置 DeepSeek，只生成一段约 10 秒的 H3 镜头提示词，不会提交 H3 任务。可选传入 `providerName`、`modelName` 和已有的 `sessionId`。 |
| `lucas_generate_video` | `prompt` 必填。将提示词提交到 Lucas Agent 当前配置的本机或远程 H3，返回任务 ID；会实际创建视频生成任务。工作流已内置，无需提供 workflow JSON 或节点 ID。 |
| `lucas_get_video_job` | `jobId` 必填。查询任务当前状态和输出。 |

## 在 Codex 中调用示例

用自然语言要求 Codex 使用对应工具即可：

1. **检查服务**：“调用 `lucas_status`，告诉我 Lucas Agent、DeepSeek 和 H3 的连接状态。”
2. **整理视频提示词**：“用 `lucas_run_agent` 的 `video` 模式，把‘秦始皇穿过神秘城门’改成一个 10 秒视频镜头提示词，先只返回提示词，不要提交生成。”
3. **提交生成任务**：“用 `lucas_generate_video` 提交刚才的提示词，并把任务 ID 告诉我。”这一步会启动实际视频生成。
4. **查询任务**：“用 `lucas_get_video_job` 查询任务 `<任务ID>` 的状态和输出。”

`lucas_run_agent` 可能会在 Lucas Agent 中创建或更新会话并调用真实模型；`lucas_generate_video` 会消耗 H3 端点的生成资源。请只在确实需要时调用生成工具。

## 进度和取消

Lucas Agent 的视频界面支持显示任务状态/进度，并在生成过程中手动终止任务。MCP 当前支持提交和查询视频任务，**尚未提供 MCP 取消工具或实时进度订阅**；需要持续查看进度或取消时，请打开 Lucas Agent 的视频任务卡片操作。

## 排错

- `Lucas Agent 本机后端未连接`：先启动 Lucas Agent，再在 Codex 重试 `lucas_status`。
- `未找到已配置 API Key 的 DeepSeek 服务商`：在 Lucas Agent 中保存可用的 DeepSeek 服务商；视频提示词模式仅使用 DeepSeek。
- H3 连接失败：在 Lucas Agent 的 AI 视频设置检查 H3 地址、Token 和连接方式，再调用 `lucas_status`。
- 找不到 MCP 工具：在 PowerShell 确认 `codex mcp list` 中有 `lucas-agent`，检查 Node.js/npm 依赖，并重启 Codex。
