import {
  HubConnectionBuilder,
  HubConnectionState,
  type HubConnection,
} from '@microsoft/signalr'
import { getBackendUrl } from './backend'

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

let connection: HubConnection | undefined

/** Creates the SignalR connection only after the native host reveals its sidecar port. */
async function getConnection(): Promise<HubConnection> {
  if (!connection) {
    const backendUrl = await getBackendUrl()
    connection = new HubConnectionBuilder()
      .withUrl(`${backendUrl}/hubs/agent`)
      .withAutomaticReconnect()
      .build()
  }
  return connection
}

export async function startAgentRun(
  sessionId: string,
  prompt: string,
  providerName: string,
  modelName: string,
  onEvent: (event: AgentEvent) => void,
) {
  const agentConnection = await getConnection()
  if (agentConnection.state === HubConnectionState.Disconnected) {
    let lastConnectionError: unknown
    let connected = false
    for (let attempt = 0; attempt < 20; attempt += 1) {
      try {
        await agentConnection.start()
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

  agentConnection.off('agentEvent')
  agentConnection.on('agentEvent', onEvent)
  await agentConnection.invoke('StartRun', sessionId, prompt, providerName, modelName)
}

/** 将用户对待执行本地工具的批准或拒绝发送回发起该运行的 SignalR 连接。 */
export async function respondToApproval(requestId: string, approved: boolean) {
  const agentConnection = await getConnection()
  if (agentConnection.state !== HubConnectionState.Connected) throw new Error('与本地 Agent 后端的连接已断开，请重试。')
  return await agentConnection.invoke<boolean>('RespondToApproval', requestId, approved)
}

/** 请求后端暂停当前 Agent 运行；若已暂停则改为停止运行。 */
export async function pauseOrStopAgentRun() {
  const agentConnection = await getConnection()
  if (agentConnection.state !== HubConnectionState.Connected) throw new Error('与本地 Agent 后端的连接已断开。')
  return await agentConnection.invoke<'paused' | 'stopping' | 'idle'>('PauseOrStop')
}
