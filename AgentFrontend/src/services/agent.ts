import {
  HubConnectionBuilder,
  HubConnectionState,
  type HubConnection,
} from '@microsoft/signalr'

export type AgentEvent = {
  type: string
  runId: string
  text?: string
  provider?: string
  model?: string
  requestId?: string
  toolName?: string
  elapsedMilliseconds?: number
  paused?: boolean
  usage?: {
    inputTokens: number
    outputTokens: number
    totalTokens: number
    estimated: boolean
  }
}

const connection: HubConnection = new HubConnectionBuilder()
  .withUrl(`${import.meta.env.VITE_BACKEND_URL || 'http://127.0.0.1:5008'}/hubs/agent`)
  .withAutomaticReconnect()
  .build()

export async function startAgentRun(
  sessionId: string,
  prompt: string,
  providerName: string,
  modelName: string,
  onEvent: (event: AgentEvent) => void,
) {
  if (connection.state === HubConnectionState.Disconnected) {
    let lastConnectionError: unknown
    let connected = false
    for (let attempt = 0; attempt < 20; attempt += 1) {
      try {
        await connection.start()
        connected = true
        break
      } catch (error) {
        lastConnectionError = error
        if (attempt === 19) break
        await new Promise((resolve) => window.setTimeout(resolve, 250))
      }
    }
    if (!connected) {
      throw lastConnectionError instanceof Error ? lastConnectionError : new Error('无法连接本地 Agent 服务。')
    }
  }

  connection.off('agentEvent')
  connection.on('agentEvent', onEvent)
  await connection.invoke('StartRun', sessionId, prompt, providerName, modelName)
}

/** 将用户对待执行本地工具的批准或拒绝发送回发起该运行的 SignalR 连接。 */
export async function respondToApproval(requestId: string, approved: boolean) {
  if (connection.state !== HubConnectionState.Connected) throw new Error('与本地 Agent 后端的连接已断开，请重试。')
  return await connection.invoke<boolean>('RespondToApproval', requestId, approved)
}

/** 请求后端暂停当前 Agent 运行；若已暂停则改为停止运行。 */
export async function pauseOrStopAgentRun() {
  if (connection.state !== HubConnectionState.Connected) throw new Error('与本地 Agent 后端的连接已断开。')
  return await connection.invoke<'paused' | 'stopping' | 'idle'>('PauseOrStop')
}
