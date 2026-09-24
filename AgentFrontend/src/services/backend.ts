import { invoke, isTauri } from '@tauri-apps/api/core'

const configuredBaseUrl = import.meta.env.VITE_BACKEND_URL?.replace(/\/$/, '')
const fallbackBaseUrl = 'http://127.0.0.1:5008'
let backendUrlPromise: Promise<string> | undefined

/** Resolves the development API URL or the dynamically assigned packaged sidecar URL. */
export function getBackendUrl(): Promise<string> {
  if (configuredBaseUrl) return Promise.resolve(configuredBaseUrl)
  if (!isTauri()) return Promise.resolve(fallbackBaseUrl)
  backendUrlPromise ??= invoke<string>('get_backend_url').catch((error) => {
    backendUrlPromise = undefined
    throw new Error(`无法获取本地后端地址：${String(error)}`)
  })
  return backendUrlPromise
}
