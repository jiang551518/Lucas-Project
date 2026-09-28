import { readFile } from 'node:fs/promises'
import path from 'node:path'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js'
import { z } from 'zod'

const fallbackUrl = 'http://127.0.0.1:5008'
const server = new McpServer({
  name: 'lucas-agent',
  version: '0.1.0',
}, {
  instructions: 'Lucas Agent runs locally. Use lucas_status first. AI-video prompts use the configured DeepSeek provider; video generation uses the configured local ComfyUI or remote Comfy API Proxy H3 endpoint with a bundled workflow. A video generation call submits a real GPU job. Never expose or request API keys through these tools.',
})

function textResult(value) {
  return { content: [{ type: 'text', text: typeof value === 'string' ? value : JSON.stringify(value, null, 2) }] }
}

async function candidateUrls() {
  const candidates = [process.env.LUCAS_AGENT_URL]
  if (process.env.LOCALAPPDATA) {
    try {
      candidates.push((await readFile(path.join(process.env.LOCALAPPDATA, 'LucasAgent', 'backend-endpoint.txt'), 'utf8')).trim())
    } catch { /* Development mode uses the stable local port. */ }
  }
  candidates.push(fallbackUrl)
  return [...new Set(candidates.map(value => value?.replace(/\/$/, '')).filter(Boolean))]
}

async function getBackendUrl() {
  const failures = []
  for (const url of await candidateUrls()) {
    try {
      const response = await fetch(`${url}/api/providers`, { signal: AbortSignal.timeout(1800) })
      if (response.ok) return url
      failures.push(`${url}: HTTP ${response.status}`)
    } catch (error) { failures.push(`${url}: ${String(error)}`) }
  }
  throw new Error(`Lucas Agent 本地后端未连接。请先启动 Lucas Agent，再重试。${failures.length ? ` (${failures.join('; ')})` : ''}`)
}

async function apiRequest(url, resource, init = {}) {
  const response = await fetch(`${url}${resource}`, {
    ...init,
    headers: { ...(init.body ? { 'Content-Type': 'application/json' } : {}), ...init.headers },
    signal: init.signal ?? AbortSignal.timeout(30000),
  })
  const body = await response.text()
  if (!response.ok) {
    let message = body
    try { message = JSON.parse(body).error ?? body } catch { /* Preserve non-JSON error text. */ }
    throw new Error(`Lucas Agent API HTTP ${response.status}: ${message}`)
  }
  if (!body) return null
  try { return JSON.parse(body) } catch { return body }
}

server.registerTool('lucas_status', {
  description: '检查 Lucas Agent 本机后端、已配置模型和本机 ComfyUI/H3 连接状态。',
  inputSchema: {},
}, async () => {
  const url = await getBackendUrl()
  const [providers, videoConfig] = await Promise.all([
    apiRequest(url, '/api/providers'),
    apiRequest(url, '/api/video/config'),
  ])
  let h3
  try { h3 = { connected: true, baseUrl: videoConfig.baseUrl, details: await apiRequest(url, '/api/video/health') } }
  catch (error) { h3 = { connected: false, baseUrl: videoConfig.baseUrl, error: String(error) } }
  return textResult({ backend: { connected: true, url }, providers, h3 })
})

server.registerTool('lucas_list_sessions', {
  description: '列出 Lucas Agent 最近的本地对话。',
  inputSchema: {},
}, async () => {
  const url = await getBackendUrl()
  return textResult(await apiRequest(url, '/api/agent/sessions'))
})

server.registerTool('lucas_run_agent', {
  description: '通过 Lucas Agent 调用已配置的模型。video 模式会使用 DeepSeek 将创意改写为单个约 10 秒 H3 视频镜头提示词；daily 模式执行一般对话。默认新建一个 Lucas Agent 会话。',
  inputSchema: {
    prompt: z.string().min(1).max(20000).describe('用户创意或问题'),
    mode: z.enum(['daily', 'video']).default('daily').describe('daily 一般对话；video 生成一个 10 秒视频镜头提示词'),
    providerName: z.string().optional().describe('日常模式可选的已配置服务商名称；省略时选择第一个有密钥的服务商'),
    modelName: z.string().optional().describe('可选模型 ID；省略时选择该服务商的第一个模型'),
    sessionId: z.string().uuid().optional().describe('可选的已有 Lucas Agent 会话 ID'),
  },
}, async ({ prompt, mode, providerName, modelName, sessionId }) => {
  const url = await getBackendUrl()
  const providers = await apiRequest(url, '/api/providers')
  const configured = providers.filter(provider => provider.hasApiKey && provider.models?.length)
  const provider = providerName
    ? configured.find(item => item.name.toLowerCase() === providerName.toLowerCase())
    : mode === 'video'
      ? configured.find(item => {
          try { return new URL(item.baseUrl).hostname.toLowerCase() === 'api.deepseek.com' } catch { return false }
        })
      : configured[0]
  if (!provider) throw new Error(mode === 'video'
    ? '未找到已配置 API Key 的 DeepSeek 服务商。请先在 Lucas Agent 设置中添加 DeepSeek。'
    : '未找到有 API Key 的模型服务商。请先在 Lucas Agent 中配置模型。')
  if (mode === 'video') {
    try {
      if (new URL(provider.baseUrl).hostname.toLowerCase() !== 'api.deepseek.com')
        throw new Error('AI 视频提示词必须使用 DeepSeek。')
    } catch (error) { throw error instanceof TypeError ? new Error('所选服务商不是 api.deepseek.com 的 DeepSeek 配置。') : error }
  }
  const model = modelName ?? provider.models[0]
  if (!provider.models.includes(model)) throw new Error(`服务商 ${provider.name} 没有模型 ${model}。`)

  const session = sessionId
    ? await apiRequest(url, `/api/agent/sessions/${encodeURIComponent(sessionId)}`)
    : await apiRequest(url, '/api/agent/sessions', { method: 'POST' })
  const runPrompt = mode === 'video'
    ? `【AI 短剧视频模式】请将用户的创意整理成一段可直接用于 MiniMax H3 的单镜头视频提示词，时长约 10 秒。提示词需描述角色外观、场景、连续动作、镜头运动、画面风格和声音/对白；若用户给的是长剧情，只聚焦一个最适合当前镜头的片段，不要一次输出多个分镜。只输出最终视频提示词，不加标题、解释或 Markdown。\n\n用户创意：\n${prompt}`
    : prompt

  const connection = new HubConnectionBuilder()
    .withUrl(`${url}/hubs/agent`)
    .configureLogging(LogLevel.Error)
    .build()
  let answer = ''
  let reasoning = ''
  let approvalRequired = ''
  let stopped = false
  let completed = false
  connection.on('agentEvent', event => {
    if (event.type === 'message.delta') answer += event.text ?? ''
    if (event.type === 'message.replace') answer = event.text ?? ''
    if (event.type === 'reasoning.delta') reasoning += event.text ?? ''
    if (event.type === 'reasoning.replace') reasoning = event.text ?? ''
    if (event.type === 'tool.approval_required') {
      approvalRequired = `${event.toolName ?? '本地工具'}: ${event.text ?? ''}`
      void (async () => {
        try { await connection.invoke('PauseOrStop'); await connection.invoke('PauseOrStop') } catch { /* The run may already be ending. */ }
      })()
    }
    if (event.type === 'run.stopped') stopped = true
    if (event.type === 'run.completed') completed = true
  })
  let timeoutId
  try {
    await connection.start()
    await Promise.race([
      connection.invoke('StartRun', session.id, runPrompt, provider.name, model),
      new Promise((_, reject) => { timeoutId = setTimeout(() => reject(new Error('Lucas Agent 运行超时（15 分钟）。')), 15 * 60 * 1000) }),
    ])
  } finally {
    if (timeoutId) clearTimeout(timeoutId)
    await connection.stop()
  }
  if (approvalRequired) throw new Error(`该对话请求执行本地工具，需要在 Lucas Agent 界面批准；MCP 已停止本轮运行。${approvalRequired}`)
  if (stopped && !completed) throw new Error(`Lucas Agent 本轮已停止。部分回答：${answer || '（暂无）'}`)
  return textResult({ sessionId: session.id, provider: provider.name, model, mode, answer, reasoning: reasoning || undefined })
})

server.registerTool('lucas_generate_video', {
  description: '让配置的 MiniMax H3 调用端点按提示词生成视频（可本机调用，也可通过 VPS 远程转发）。工作流已内置，不需要提供 JSON 或节点 ID；会占用 H3 主机的 GPU，仅在用户明确要求生成时调用。',
  inputSchema: {
    prompt: z.string().min(1).max(12000).describe('H3 单镜头视频提示词'),
  },
}, async ({ prompt }) => {
  const url = await getBackendUrl()
  const job = await apiRequest(url, '/api/video/jobs', { method: 'POST', body: JSON.stringify({ prompt }) })
  return textResult({ submitted: true, job })
})

server.registerTool('lucas_get_video_job', {
  description: '查询一个本机 H3 视频任务的当前状态和输出信息。',
  inputSchema: { jobId: z.string().min(1).max(100).regex(/^[a-zA-Z0-9_-]+$/).describe('H3 返回的任务 ID') },
}, async ({ jobId }) => {
  const url = await getBackendUrl()
  return textResult(await apiRequest(url, `/api/video/jobs/${encodeURIComponent(jobId)}`))
})

await server.connect(new StdioServerTransport())
