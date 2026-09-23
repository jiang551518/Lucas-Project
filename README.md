# Lucas Agent

Lucas Agent 是一个纯本地运行的桌面 AI Agent 原型，目标是让用户在本机工作区内与不同 AI 模型协作。应用采用 Vue 3 + TypeScript 前端、Tauri 2 桌面壳和 ASP.NET Core 后端；本地数据保存在 SQLite，不需要登录或云端同步。

> 当前处于开发阶段。Tauri 开发模式可以启动或复用前后端；正式发行包尚未把 .NET 后端配置为 sidecar，因此目前不能视为完整的独立安装包。

## 功能

- **对话与模型**：通过 OpenAI Chat Completions 流式接口连接用户配置的模型服务商；自定义服务商名称、Base URL、API Key 和模型列表，按厂商分组选择。
- **DeepSeek 支持**：支持 DeepSeek 账户余额查询（可用状态、总余额、赠金和充值余额）；可处理 OpenAI `tool_calls`，并兼容模型输出到正文中的 DeepSeek DSML 工具调用格式。
- **会话用量与耗时**：显示当前会话累计输入/输出 Token；数据库按每条 assistant 回复保存本轮用量。模型未提供 usage 时会估算并标注。回复过程显示运行状态和耗时，完成后保留本轮用时。
- **工作区项目**：项目可关联本地目录或单个文件；对话可以归入项目或移回最近列表，并支持重命名和删除。
- **本地工具与权限**：Agent 可列举、读取、写入工作区文件，也可运行终端命令。权限可设为“请求用户批准”或“AI 完全访问”；Windows 使用 PowerShell，macOS/Linux 使用 Bash。
- **代码变更预览**：显示一轮运行前后的 Git diff，便于检查 Agent 对工作区所做的更改。
- **Harness / 工具循环**：模型可以根据任务选择工具，并将工具结果带回后续模型请求；每次运行有有限的工具往返次数，避免无限循环。
- **技能、记忆和设置**：管理本地技能和记忆；设置思考过程、Token 用量显示及新会话默认审批偏好。

### 安全说明

本地命令由当前桌面用户的系统账户执行。工作区限制用于文件工具的路径校验，不是操作系统级沙箱；命令行程序可能访问工作区以外的资源。请在批准命令或启用“AI 完全访问”前检查任务和命令。API Key 使用 ASP.NET Core Data Protection 加密后保存在本机数据库旁的应用数据目录中；不要提交数据库或密钥文件。

## 技术结构

```text
Vue 3 + TypeScript
  ├─ REST：项目、会话、模型配置、技能、记忆、设置、余额
  └─ SignalR：消息/思考增量、工具状态、审批、Token 用量、运行耗时、Git diff
Tauri 2：桌面窗口与原生文件/目录选择
ASP.NET Core
  ├─ Controller → IAgentService → AgentService
  └─ AgentService → IAgentRepository → SqliteAgentRepository
SQLite：项目、会话、消息、模型服务商、技能、记忆和设置
```

默认数据库路径：

- Windows：`%LOCALAPPDATA%\LucasAgent\workspace.db`
- macOS/Linux：系统提供的用户级 Local Application Data 目录下的 `LucasAgent/workspace.db`

## 本机开发

### 环境要求

- .NET 8 SDK
- Node.js（含 npm）
- Rust stable 工具链
- Windows 桌面构建需要 Visual Studio C++ Desktop workload 和 WebView2；其他平台需安装对应平台的 Tauri 系统依赖

### 分别启动前后端

在项目根目录打开两个终端。

终端一启动后端：

```powershell
dotnet run --project .\AgentBackend\AgentBackend\AgentBackend\AgentBackend.csproj --no-launch-profile --urls http://127.0.0.1:5008
```

终端二启动前端：

```powershell
Set-Location .\AgentFrontend
npm install
npm run dev
```

然后打开 <http://localhost:5173>。macOS/Linux 可在终端一使用相同的 `dotnet run` 命令，在终端二使用 `cd AgentFrontend && npm install && npm run dev`。

前端默认连接 `http://127.0.0.1:5008`。如需更改后端地址，可在 `AgentFrontend/.env.local` 设置 `VITE_BACKEND_URL`。

### Tauri 桌面开发

在前端目录运行：

```powershell
npm.cmd run tauri:dev
```

`tauri:dev` 会检查并复用已运行的 `5173` 前端和 `5008` 后端；缺少时会启动对应服务。macOS/Linux 使用 `npm run tauri:dev`。退出桌面开发进程时，它会停止自己启动的服务，不会主动停止原先已运行的服务。

构建桌面前端/壳：

```powershell
npm.cmd run tauri:build
```

macOS/Linux 使用 `npm run tauri:build`。各平台安装包需要在对应平台构建。当前 Tauri 配置尚未将 ASP.NET Core 后端打包为 sidecar；制作独立发行包前需补齐后端发布、sidecar 生命周期管理及安装包验证。

## 配置 AI 服务商

在聊天输入框的模型选择菜单中添加服务商，填写：

1. 配置名称（自定义）；
2. OpenAI-compatible Base URL；
3. 一个或多个模型 ID；
4. API Key。

服务商和模型列表保存在本地 SQLite 中，API Key 不会被 API 返回给前端。余额查询目前仅接受 `https://api.deepseek.com` 域名下的已保存配置，并使用 DeepSeek 官方 `GET /user/balance` 接口。

模型协议兼容能力取决于具体服务商和模型。当前 Agent 请求使用流式 Chat Completions；工具调用优先采用标准 OpenAI `tool_calls`，并适配 DeepSeek DSML 输出。并非所有 OpenAI-compatible 服务都支持工具调用、流式 usage 或思考字段。未配置有效 API Key 时进入演示模式，不会调用真实模型。

## API 概览

### 项目、会话与执行上下文

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `GET` | `/api/projects` | 获取项目列表 |
| `POST` | `/api/projects` | 创建项目 |
| `PUT` | `/api/projects/{id}/name` | 重命名项目 |
| `DELETE` | `/api/projects/{id}` | 删除项目，保留对话并移回最近 |
| `GET` | `/api/agent/sessions` | 获取会话列表 |
| `POST` | `/api/agent/sessions` | 创建空白会话 |
| `GET` | `/api/agent/sessions/{id}` | 获取会话和消息 |
| `PUT` | `/api/agent/sessions/{id}/project` | 关联项目或解除关联 |
| `PUT` | `/api/agent/sessions/{id}/title` | 重命名会话 |
| `PUT` | `/api/agent/sessions/{id}/execution-context` | 更新工作区与权限模式 |
| `DELETE` | `/api/agent/sessions/{id}` | 删除会话及消息 |

### 模型配置、技能、记忆和设置

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `GET` | `/api/providers` | 获取服务商/模型列表，不含密钥 |
| `PUT` | `/api/providers` | 新增或更新服务商配置 |
| `GET` | `/api/providers/{name}/balance` | 查询 DeepSeek 账户余额 |
| `DELETE` | `/api/providers/{name}` | 删除服务商及模型 |
| `GET` / `PUT` | `/api/skills` | 读取或保存技能 |
| `DELETE` | `/api/skills/{id}` | 删除技能 |
| `GET` / `PUT` | `/api/memories` | 读取或保存记忆 |
| `DELETE` | `/api/memories/{id}` | 删除记忆 |
| `GET` / `PUT` | `/api/settings` | 读取或保存设置 |

Agent 对话通过 SignalR Hub `/hubs/agent` 启动，并以统一事件传递回答、思考、工具状态、审批请求、Token 用量、耗时和 Git diff。主要事件包括 `run.started`、`reasoning.delta`、`message.delta`、`tool.started`、`tool.approval_required`、`usage.update`、`run.git_diff` 和 `run.completed`。

## 当前限制

- 仍是快速迭代中的 MVP，模型兼容性应按“服务商 + 模型”单独验证。
- 工具调用会真实访问或修改本地工作区；Git diff 用于查看变更，不会自动替用户提交 Git commit。
- 自动测试覆盖、多平台 CI、正式 Tauri + .NET sidecar 发布流程尚未完善。

## GitHub

项目仓库：[jiang551518/Lucas-Project](https://github.com/jiang551518/Lucas-Project)
