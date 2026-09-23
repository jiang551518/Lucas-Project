# Lucas Agent

一个以桌面编码 Agent 为目标的跨平台应用 MVP。当前包含 Vue 前端、ASP.NET Core 后端、SignalR 流式对话、OpenAI-compatible Provider 适配，以及本地工作区持久化。Tauri 桌面壳、文件/终端工具、权限确认、Harness 自动验证仍是后续阶段。

## 架构

```text
Vue 3 + TypeScript
  ├─ REST：项目、会话与项目关联
  └─ SignalR：思考/回答增量、运行状态和 Token 用量事件
ASP.NET Core
  Controller → IService → IRepository → SQLite 关系表
  AgentService → user-managed OpenAI-compatible providers and models (SSE)
```

后端代码遵循 Controller → IService → IRepository；后端方法使用 XML 摘要注释说明职责。项目、会话、消息、服务商、技能、记忆和设置保存在当前系统用户应用数据目录的 `LucasAgent/workspace.db` 中。

## 启动

需要 .NET 8 SDK、Node.js 和 npm。

终端一启动后端：

```powershell
cd 'D:\Lucas Project\AgentBackend\AgentBackend\AgentBackend'
dotnet run --no-launch-profile --urls http://127.0.0.1:5008
```

终端二启动前端：

```powershell
cd 'D:\Lucas Project\AgentFrontend'
npm install
npm run dev
```

打开 `http://localhost:5173`。前端默认连接 `http://127.0.0.1:5008`；需要改地址时，设置 `AgentFrontend/.env.local` 中的 `VITE_BACKEND_URL`。

## Tauri 桌面开发（Windows）

已接入 Tauri 2。安装 Rust stable MSVC toolchain、Visual Studio C++ Desktop workload 和 WebView2 后，在前端目录运行：

```powershell
npm.cmd run tauri:dev
```

该命令会复用已运行的 Vite/API 服务，并仅启动缺少的服务。打包命令为 `npm.cmd run tauri:build`。Windows/macOS/Linux 安装包须分别在对应系统构建；正式发行时还需将 .NET 后端作为 sidecar 一并打包，目前此骨架的桌面开发模式会从源码目录启动后端。

## 配置模型

服务商与模型不再写在 `appsettings.json`。在输入框的模型菜单中选择“添加服务商”，填写自定义厂商名称、OpenAI-compatible Base URL、API Key 和一个或多个模型 ID。配置保存在本机 SQLite 数据库；API Key 使用 ASP.NET Data Protection 加密。模型菜单按厂商分组展示，服务商设置中可添加、编辑或删除模型。真实请求采用 OpenAI Chat Completions SSE 格式；没有 API Key 时可使用不鉴权的兼容服务。当前 Token 用量优先显示上游返回值；不返回用量的兼容服务会使用字符比例估算并明确标记“估算”。是否能显示思考内容由服务商及具体模型的流式协议决定。

## 当前 API

```text
GET    /api/projects
POST   /api/projects                       { "name": "项目名" }
DELETE /api/projects/{id}                   删除项目并保留其中的对话
GET    /api/providers                      返回厂商和模型分组（不含密钥）
PUT    /api/providers                      保存/重命名厂商和模型列表（API Key 加密存储）
DELETE /api/providers/{name}               删除厂商及其模型
GET    /api/agent/sessions
POST   /api/agent/sessions
GET    /api/agent/sessions/{id}
PUT    /api/agent/sessions/{id}/project    { "projectId": "GUID 或 null" }
DELETE /api/agent/sessions/{id}             删除会话及消息历史
SignalR /hubs/agent                        StartRun(sessionId, prompt, providerName, modelName)
```

SignalR 统一事件：`run.started`、`reasoning.delta`、`message.delta`、`usage.update`、`run.completed`。项目、会话、消息及模型用量保存在本地 SQLite 数据库中。

## 尚未实现

- Windows/macOS/Linux 安装包与 .NET sidecar 正式打包；
- Agent 文件读写、Patch、Shell/Git 工具及逐项权限确认；
- Harness 编译/测试/修复循环、取消运行、会话删除/重命名；
- 自动化测试和多平台 CI。
