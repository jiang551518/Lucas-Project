import { getBackendUrl } from './backend'

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response | undefined
  let lastConnectionError: unknown
  for (let attempt = 0; attempt < 20; attempt += 1) {
    try {
      const apiBase = await getBackendUrl()
      response = await fetch(`${apiBase}${path}`, {
        ...init,
        headers: { 'Content-Type': 'application/json', ...init?.headers },
      })
      break
    } catch (error) {
      lastConnectionError = error
      if (attempt === 19) break
      await new Promise((resolve) => window.setTimeout(resolve, 250))
    }
  }
  if (!response) throw lastConnectionError instanceof Error ? lastConnectionError : new Error('无法连接本地 Agent 服务。')
  if (!response.ok) {
    const body = await response.text()
    throw new Error(body || `HTTP ${response.status}`)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export type WorkspaceProject = { id: string; name: string; createdAt: string; workspacePath: string; workspacePathType: 'directory' | 'file' }
export type WorkspaceMessage = { id: string; role: string; content: string; reasoning?: string; createdAt: string; inputTokens?: number; outputTokens?: number; elapsedMilliseconds?: number; gitDiff?: string }
export type WorkspaceSession = {
  id: string
  title: string
  projectId: string | null
  workspacePath: string
  workspacePathType: 'directory' | 'file'
  permissionMode: 'approval' | 'fullAccess'
  updatedAt: string
  messages: WorkspaceMessage[]
}

/** 从后端读取持久化的项目列表。 */
export const getProjects = () => request<WorkspaceProject[]>('/api/projects')
/** 创建后端持久化项目。 */
export const createProject = (name: string, workspacePath: string, workspacePathType: 'directory' | 'file') => request<WorkspaceProject>('/api/projects', { method: 'POST', body: JSON.stringify({ name, workspacePath, workspacePathType }) })
/** 持久化更新项目名称。 */
export const renameProject = (projectId: string, name: string) => request<void>(`/api/projects/${projectId}/name`, { method: 'PUT', body: JSON.stringify({ name }) })
/** 从后端读取最近会话。 */
export const getSessions = () => request<WorkspaceSession[]>('/api/agent/sessions')
/** 在后端创建新会话。 */
export const createSession = () => request<WorkspaceSession>('/api/agent/sessions', { method: 'POST' })
/** 更新会话所属项目；传 null 时解除关联。 */
export const assignSession = (sessionId: string, projectId: string | null) =>
  request<void>(`/api/agent/sessions/${sessionId}/project`, { method: 'PUT', body: JSON.stringify({ projectId }) })
/** 持久化会话的工作区范围与本地工具权限模式。 */
export const updateExecutionContext = (sessionId: string, context: { projectId: string | null; workspacePath: string; workspacePathType: 'directory' | 'file'; permissionMode: 'approval' | 'fullAccess' }) =>
  request<void>(`/api/agent/sessions/${sessionId}/execution-context`, { method: 'PUT', body: JSON.stringify(context) })
/** 持久化更新会话标题。 */
export const renameSession = (sessionId: string, name: string) => request<void>(`/api/agent/sessions/${sessionId}/title`, { method: 'PUT', body: JSON.stringify({ name }) })
/** 永久删除会话及其消息历史。 */
export const deleteSession = (sessionId: string) => request<void>(`/api/agent/sessions/${sessionId}`, { method: 'DELETE' })
/** 删除项目并由后端把其中的对话保留在最近列表。 */
export const deleteProject = (projectId: string) => request<void>(`/api/projects/${projectId}`, { method: 'DELETE' })
/** 获取不含密钥的模型服务配置摘要。 */
export const getProviders = () => request<ProviderSummary[]>('/api/providers')
/** 安全保存用户配置的 OpenAI-compatible Provider 与模型集合。 */
export const saveProvider = (provider: SaveProviderRequest) => request<ProviderSummary>('/api/providers', { method: 'PUT', body: JSON.stringify(provider) })
/** 删除用户添加的 Provider 配置及其全部模型。 */
export const deleteProvider = (name: string) => request<void>(`/api/providers/${encodeURIComponent(name)}`, { method: 'DELETE' })
/** 查询已保存的 DeepSeek 服务商账号余额。 */
export const getProviderBalance = (name: string) => request<ProviderBalance>(`/api/providers/${encodeURIComponent(name)}/balance`)
export type ProviderSummary = { name: string; baseUrl: string; models: string[]; hasApiKey: boolean }
export type ProviderBalance = { isAvailable: boolean; balanceInfos: { currency: string; totalBalance: string; grantedBalance: string; toppedUpBalance: string }[] }
export type SaveProviderRequest = { name: string; existingName: string | null; baseUrl: string; models: string[]; apiKey: string }
export type AgentSkill = { id: string; name: string; description: string; instructions: string; enabled: boolean; updatedAt: string }
export type MemoryEntry = { id: string; title: string; content: string; enabled: boolean; updatedAt: string }
export type AgentSettings = { showReasoning: boolean; showTokenUsage: boolean; requireToolApproval: boolean }
/** 读取本地保存的技能条目。 */
export const getSkills = () => request<AgentSkill[]>('/api/skills')
/** 新增或更新本地技能条目。 */
export const saveSkill = (skill: AgentSkill) => request<AgentSkill>('/api/skills', { method: 'PUT', body: JSON.stringify(skill) })
/** 删除本地技能条目。 */
export const deleteSkill = (id: string) => request<void>(`/api/skills/${id}`, { method: 'DELETE' })
/** 读取本地记忆条目。 */
export const getMemories = () => request<MemoryEntry[]>('/api/memories')
/** 新增或更新记忆条目。 */
export const saveMemory = (memory: MemoryEntry) => request<MemoryEntry>('/api/memories', { method: 'PUT', body: JSON.stringify(memory) })
/** 删除本地记忆条目。 */
export const deleteMemory = (id: string) => request<void>(`/api/memories/${id}`, { method: 'DELETE' })
/** 读取本地显示与 Harness/Loop 配置。 */
export const getSettings = () => request<AgentSettings>('/api/settings')
/** 保存本地显示与 Harness/Loop 配置。 */
export const saveSettings = (settings: AgentSettings) => request<AgentSettings>('/api/settings', { method: 'PUT', body: JSON.stringify(settings) })
