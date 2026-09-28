import { getBackendUrl } from './backend'

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const baseUrl = await getBackendUrl()
  const response = await fetch(`${baseUrl}${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  })
  if (!response.ok) {
    const body = await response.text()
    throw new Error(body || `HTTP ${response.status}`)
  }
  return response.json() as Promise<T>
}

export type VideoAgentConfig = { baseUrl: string; hasApiToken: boolean }
export type SaveVideoAgentConfig = { baseUrl: string; apiToken: string }
export const getVideoAgentConfig = () => request<VideoAgentConfig>('/api/video/config')
export const saveVideoAgentConfig = (config: SaveVideoAgentConfig) =>
  request<VideoAgentConfig>('/api/video/config', { method: 'PUT', body: JSON.stringify(config) })
export const checkVideoApiHealth = () => request<Record<string, unknown>>('/api/video/health')
export const submitVideoJob = (prompt: string) =>
  request<Record<string, unknown>>('/api/video/jobs', { method: 'POST', body: JSON.stringify({ prompt }) })
export const getVideoJob = (jobId: string) => request<Record<string, unknown>>(`/api/video/jobs/${encodeURIComponent(jobId)}`)
export const cancelVideoJob = (jobId: string) =>
  request<Record<string, unknown>>(`/api/video/jobs/${encodeURIComponent(jobId)}/cancel`, { method: 'POST' })

export type VideoJobEvent = { event: string; data: Record<string, unknown> }
export async function streamVideoJobEvents(jobId: string, onEvent: (event: VideoJobEvent) => void, signal: AbortSignal) {
  const baseUrl = await getBackendUrl()
  const response = await fetch(`${baseUrl}/api/video/jobs/${encodeURIComponent(jobId)}/events`, {
    headers: { Accept: 'text/event-stream' },
    signal,
  })
  if (!response.ok) {
    const body = await response.text()
    throw new Error(body || `SSE HTTP ${response.status}`)
  }
  if (!response.body) throw new Error('浏览器不支持读取视频进度流。')

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  let eventName = 'message'
  let dataLines: string[] = []
  const dispatch = () => {
    if (!dataLines.length) return
    try {
      const data = JSON.parse(dataLines.join('\n')) as Record<string, unknown>
      onEvent({ event: eventName, data })
    } catch { /* Ignore malformed or non-JSON SSE payloads. */ }
    eventName = 'message'
    dataLines = []
  }
  const consumeLine = (line: string) => {
    if (!line) { dispatch(); return }
    if (line.startsWith(':')) return
    const separator = line.indexOf(':')
    const field = separator < 0 ? line : line.slice(0, separator)
    const value = separator < 0 ? '' : line.slice(separator + 1).replace(/^ /, '')
    if (field === 'event') eventName = value || 'message'
    else if (field === 'data') dataLines.push(value)
  }

  while (true) {
    const { value, done } = await reader.read()
    buffer += decoder.decode(value, { stream: !done })
    let newline = buffer.indexOf('\n')
    while (newline >= 0) {
      consumeLine(buffer.slice(0, newline).replace(/\r$/, ''))
      buffer = buffer.slice(newline + 1)
      newline = buffer.indexOf('\n')
    }
    if (done) {
      if (buffer) consumeLine(buffer.replace(/\r$/, ''))
      dispatch()
      return
    }
  }
}
