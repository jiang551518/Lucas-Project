<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, onUpdated, ref } from 'vue'
import { marked } from 'marked'
import DOMPurify from 'dompurify'
import { isTauri } from '@tauri-apps/api/core'
import { getCurrentWindow } from '@tauri-apps/api/window'
import { open as openNativeDialog } from '@tauri-apps/plugin-dialog'
import {
  IconBook2, IconChevronDown, IconChevronRight, IconDots, IconFolder,
  IconMaximize, IconMessagePlus, IconMinimize, IconPencil, IconPlus, IconSearch, IconSettings, IconSparkles, IconTrash, IconX,
} from '@tabler/icons-vue'
import { pauseOrStopAgentRun, respondToApproval, startAgentRun, type AgentEvent } from './services/agent'
import { createProject as createProjectRequest, createSession, deleteProject as deleteProjectRequest, deleteProvider, deleteSession, deleteSkill, deleteMemory, getProjects, getProviders, getProviderBalance, getSessions, getSkills, getMemories, getSettings, renameProject as renameProjectRequest, renameSession as renameSessionRequest, saveProvider, saveSkill, saveMemory, saveSettings as saveSettingsRequest, updateExecutionContext, type AgentSkill, type AgentSettings, type MemoryEntry, type ProviderBalance, type ProviderSummary, type WorkspaceProject, type WorkspaceSession } from './services/workspace'

type Chat = WorkspaceSession & { updatedAt: string; messages: (WorkspaceSession['messages'][number] & { elapsedMilliseconds?: number; startedAtMs?: number; toolStatus?: string; showGitDiff?: boolean })[] }
type Project = WorkspaceProject & { expanded: boolean }
type WorkspaceDialog = { kind: 'delete-chat' | 'delete-project' | 'rename-chat' | 'rename-project'; id: string; name: string; relatedCount?: number }
const projects = ref<Project[]>([])
const chats = ref<Chat[]>([])
const providers = ref<ProviderSummary[]>([])
const skills = ref<AgentSkill[]>([])
const memories = ref<MemoryEntry[]>([])
const settings = ref<AgentSettings>({ showReasoning: true, showTokenUsage: true, requireToolApproval: true })
const activeView = ref<'chat' | 'skills' | 'memories' | 'settings'>('chat')
const skillDraft = ref<AgentSkill | null>(null)
const memoryDraft = ref<MemoryEntry | null>(null)
const settingsSaving = ref(false)

const selectedProviderName = ref('')
const selectedModel = ref('')
const modelPickerLabel = computed(() => selectedModel.value || '添加 AI 服务商')
const showModelMenu = ref(false)
const prompt = ref('')
const activeChatId = ref('')
const openChatMenuId = ref<string | null>(null)
const openProjectMenuId = ref<string | null>(null)
const workspaceDialog = ref<WorkspaceDialog | null>(null)
const workspaceDialogName = ref('')
const workspaceDialogBusy = ref(false)
const isCreatingProject = ref(false)
const projectName = ref('')
const projectPath = ref('')
const projectPathType = ref<'directory' | 'file'>('directory')
const selectedWorkspacePath = ref('')
const selectedWorkspacePathType = ref<'directory' | 'file'>('directory')
const selectedWorkspaceProjectId = ref<string | null>(null)
const permissionMode = ref<'approval' | 'fullAccess'>('approval')
const workspaceMenuOpen = ref(false)
const permissionMenuOpen = ref(false)
const selectedWorkspaceLabel = computed(() => selectedWorkspacePath.value ? selectedWorkspacePath.value.split(/[\\/]/).filter(Boolean).slice(-1)[0] || selectedWorkspacePath.value : '未选择工作区')
const workspaceError = ref('')
const isRunning = ref(false)
const runPaused = ref(false)
const runStopping = ref(false)
const runtimeTick = ref(Date.now())
const approvalRequest = ref<{ requestId: string; toolName: string; details: string } | null>(null)
const approvalBusy = ref(false)
const usage = ref({ inputTokens: 0, outputTokens: 0, estimated: true })
const showProviderSettings = ref(false)
const providerSaving = ref(false)
const providerDraft = ref({ existingName: '', name: '', baseUrl: '', models: [''], apiKey: '' })
const providerHasKey = ref(false)
const providerBalance = ref<ProviderBalance | null>(null)
const providerBalanceError = ref('')
const providerBalanceLoading = ref(false)
const providerBalanceName = ref('')
const activeChat = computed(() => chats.value.find(chat => chat.id === activeChatId.value))
const sidebarBalanceProvider = computed(() =>
  providers.value.find(provider => provider.name === selectedProviderName.value && provider.hasApiKey && isDeepSeekBaseUrl(provider.baseUrl))
  ?? providers.value.find(provider => provider.hasApiKey && isDeepSeekBaseUrl(provider.baseUrl))
)
const unassignedChats = computed(() => chats.value.filter(chat => !chat.projectId).sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt)))
const visibleProject = computed(() => {
  const projectId = activeChat.value ? activeChat.value.projectId : selectedWorkspaceProjectId.value
  return projects.value.find(project => project.id === projectId)
})
const projectContextLabel = computed(() => visibleProject.value?.name || (selectedWorkspacePath.value ? '自定义工作区' : activeChat.value ? '未关联项目' : ''))
const outputFontSize = ref(Math.min(22, Math.max(13, Number(localStorage.getItem('lucas-output-font-size')) || 16)))
const showSearch = ref(false)
const searchText = ref('')
const searchInput = ref<HTMLInputElement | null>(null)
const isFullscreen = ref(false)
const conversationElement = ref<HTMLElement | null>(null)
const showScrollToBottom = ref(false)
let scrollVisibilityFrame: number | undefined

onMounted(async () => {
  document.addEventListener('click', handleOutsideMenuClick)
  document.addEventListener('fullscreenchange', syncFullscreenState)
  if (isTauri()) {
    try { isFullscreen.value = await getCurrentWindow().isFullscreen() } catch { /* Keep the browser fallback state. */ }
  }
  try {
    const [serverProjects, serverSessions, serverProviders, serverSkills, serverMemories, serverSettings] = await Promise.all([getProjects(), getSessions(), getProviders(), getSkills(), getMemories(), getSettings()])
    projects.value = serverProjects.map(project => ({ ...project, expanded: true }))
    chats.value = serverSessions
    providers.value = serverProviders
    skills.value = serverSkills
    memories.value = serverMemories
    settings.value = serverSettings
    const firstProvider = serverProviders.find(provider => provider.models.length > 0)
    if (firstProvider) selectModel(firstProvider.name, firstProvider.models[0])
    const initialChat = chats.value.find(chat => chat.messages.length > 0)
    if (initialChat) openChat(initialChat)
  } catch (error) { workspaceError.value = `无法连接后端：${String(error)}` }
})
onUpdated(scheduleScrollVisibilityUpdate)
onBeforeUnmount(() => { document.removeEventListener('click', handleOutsideMenuClick); document.removeEventListener('fullscreenchange', syncFullscreenState); if (runtimeTimer) clearInterval(runtimeTimer); if (scrollVisibilityFrame !== undefined) cancelAnimationFrame(scrollVisibilityFrame) })
let runtimeTimer: ReturnType<typeof setInterval> | undefined

function updateScrollToBottomVisibility() {
  const element = conversationElement.value
  if (!element) { showScrollToBottom.value = false; return }
  const remaining = element.scrollHeight - element.scrollTop - element.clientHeight
  showScrollToBottom.value = element.scrollHeight > element.clientHeight + 32 && remaining > 80
}
function scheduleScrollVisibilityUpdate() {
  if (scrollVisibilityFrame !== undefined) cancelAnimationFrame(scrollVisibilityFrame)
  scrollVisibilityFrame = requestAnimationFrame(() => {
    scrollVisibilityFrame = undefined
    updateScrollToBottomVisibility()
  })
}
function scrollConversationToBottom() {
  const element = conversationElement.value
  if (!element) return
  element.scrollTo({ top: element.scrollHeight, behavior: 'smooth' })
}

function formatDuration(milliseconds: number) {
  const totalSeconds = Math.max(0, Math.floor(milliseconds / 1000))
  const hours = Math.floor(totalSeconds / 3600)
  const minutes = Math.floor((totalSeconds % 3600) / 60)
  const seconds = totalSeconds % 60
  return hours > 0 ? `${hours}小时 ${minutes}分 ${seconds}秒` : minutes > 0 ? `${minutes}分 ${seconds}秒` : `${seconds}秒`
}
function messageDuration(message: Chat['messages'][number]) {
  void runtimeTick.value
  return message.elapsedMilliseconds ?? (message.startedAtMs ? runtimeTick.value - message.startedAtMs : 0)
}
async function decideToolApproval(approved: boolean) {
  const request = approvalRequest.value
  if (!request || approvalBusy.value) return
  approvalBusy.value = true
  try {
    const accepted = await respondToApproval(request.requestId, approved)
    if (!accepted) workspaceError.value = '该审批请求已失效或已处理。'
    approvalRequest.value = null
  } catch (error) { workspaceError.value = `提交审批失败：${String(error)}` }
  finally { approvalBusy.value = false }
}
async function pauseOrStop() {
  if (!isRunning.value || runStopping.value) return
  try {
    const result = await pauseOrStopAgentRun()
    runPaused.value = result === 'paused'
    runStopping.value = result === 'stopping'
    const currentMessages = activeChat.value?.messages
    const assistant = currentMessages?.[currentMessages.length - 1]
    if (assistant?.role === 'assistant') assistant.toolStatus = runPaused.value ? '已暂停 · 再次点击停止' : runStopping.value ? '正在停止…' : assistant.toolStatus
  } catch (error) { workspaceError.value = `暂停或停止任务失败：${String(error)}` }
}

function handleOutsideMenuClick(event: MouseEvent) {
  const target = event.target
  if (target instanceof Element && target.closest('.workspace-picker, .permission-picker, .project-menu, .project-menu-trigger, .chat-assignment-menu, .chat-menu-trigger')) return
  workspaceMenuOpen.value = false
  permissionMenuOpen.value = false
  openProjectMenuId.value = null
  openChatMenuId.value = null
}

async function createProject() {
  const name = projectName.value.trim()
  if (!name || !projectPath.value) return
  try {
    const project = await createProjectRequest(name, projectPath.value, projectPathType.value)
    projects.value.push({ ...project, expanded: true })
    projectName.value = ''
    projectPath.value = ''
    isCreatingProject.value = false
  } catch (error) { workspaceError.value = `创建项目失败：${String(error)}` }
}
async function chooseProjectWorkspace() {
  if (!isTauri()) { workspaceError.value = '请选择在 Lucas Agent 桌面程序中使用系统文件选择器。'; return }
  try {
    const selection = await openNativeDialog({ directory: projectPathType.value === 'directory', multiple: false, title: projectPathType.value === 'directory' ? '选择项目工作目录' : '选择工作文件' })
    if (typeof selection === 'string') projectPath.value = selection
  } catch (error) { workspaceError.value = `选择项目工作区失败：${String(error)}` }
}
function renderMarkdown(source: string) {
  const html = marked.parse(source, { async: false, breaks: true, gfm: true }) as string
  return DOMPurify.sanitize(html, { USE_PROFILES: { html: true } })
}
function newSkill() { skillDraft.value = { id: crypto.randomUUID(), name: '', description: '', instructions: '', enabled: true, updatedAt: new Date().toISOString() } }
function editSkill(skill: AgentSkill) { skillDraft.value = { ...skill } }
async function persistSkill() {
  if (!skillDraft.value) return
  try { const saved = await saveSkill(skillDraft.value); skills.value = [saved, ...skills.value.filter(item => item.id !== saved.id)]; skillDraft.value = null }
  catch (error) { workspaceError.value = `保存技能失败：${String(error)}` }
}
async function removeSkill(skill: AgentSkill) {
  try { await deleteSkill(skill.id); skills.value = skills.value.filter(item => item.id !== skill.id) }
  catch (error) { workspaceError.value = `删除技能失败：${String(error)}` }
}
function newMemory() { memoryDraft.value = { id: crypto.randomUUID(), title: '', content: '', enabled: true, updatedAt: new Date().toISOString() } }
function editMemory(memory: MemoryEntry) { memoryDraft.value = { ...memory } }
async function persistMemory() {
  if (!memoryDraft.value) return
  try { const saved = await saveMemory(memoryDraft.value); memories.value = [saved, ...memories.value.filter(item => item.id !== saved.id)]; memoryDraft.value = null }
  catch (error) { workspaceError.value = `保存记忆失败：${String(error)}` }
}
async function removeMemory(memory: MemoryEntry) {
  try { await deleteMemory(memory.id); memories.value = memories.value.filter(item => item.id !== memory.id) }
  catch (error) { workspaceError.value = `删除记忆失败：${String(error)}` }
}
async function persistSettings() {
  settingsSaving.value = true
  try { settings.value = await saveSettingsRequest(settings.value); workspaceError.value = '' }
  catch (error) { workspaceError.value = `保存设置失败：${String(error)}` }
  finally { settingsSaving.value = false }
}
async function createChat() {
  try {
    const chat = await createSession()
    // 新对话创建前也可以先选项目，因此必须在第一次运行前把上下文写入会话。
    try {
      await updateExecutionContext(chat.id, {
        projectId: selectedWorkspaceProjectId.value,
        workspacePath: selectedWorkspacePath.value,
        workspacePathType: selectedWorkspacePathType.value,
        permissionMode: permissionMode.value,
      })
    } catch (error) {
      try { await deleteSession(chat.id) } catch { /* Avoid leaving an unusable empty session behind. */ }
      workspaceError.value = `新对话的工作区设置未能保存：${String(error)}`
      return null
    }
    chat.projectId = selectedWorkspaceProjectId.value
    chat.workspacePath = selectedWorkspacePath.value
    chat.workspacePathType = selectedWorkspacePathType.value
    chat.permissionMode = permissionMode.value
    chats.value.unshift(chat)
    activeChatId.value = chat.id
    return chat
  } catch (error) {
    workspaceError.value = `创建对话失败：${String(error)}`
    return null
  }
}
function openChat(chat: Chat) {
  activeView.value = 'chat'
  activeChatId.value = chat.id
  openChatMenuId.value = null
  openProjectMenuId.value = null
  const project = projects.value.find(item => item.id === chat.projectId)
  selectedWorkspaceProjectId.value = chat.projectId
  selectedWorkspacePath.value = project?.workspacePath || chat.workspacePath || ''
  selectedWorkspacePathType.value = project?.workspacePathType || chat.workspacePathType || 'directory'
  permissionMode.value = chat.permissionMode === 'fullAccess' ? 'fullAccess' : 'approval'
  usage.value = getSessionUsage(chat)
  workspaceMenuOpen.value = false
  permissionMenuOpen.value = false
}
function getSessionUsage(chat: Chat) {
  const assistantMessages = chat.messages.filter(message => message.role === 'assistant')
  return {
    inputTokens: assistantMessages.reduce((total, message) => total + (message.inputTokens ?? 0), 0),
    outputTokens: assistantMessages.reduce((total, message) => total + (message.outputTokens ?? 0), 0),
    // Older databases do not record whether a stored turn's count was estimated.
    estimated: assistantMessages.some(message => (message.inputTokens ?? 0) > 0 || (message.outputTokens ?? 0) > 0),
  }
}
function setProjectWorkspace(project: Project | null) {
  selectedWorkspaceProjectId.value = project?.id ?? null
  selectedWorkspacePath.value = project?.workspacePath ?? ''
  selectedWorkspacePathType.value = project?.workspacePathType ?? 'directory'
  workspaceMenuOpen.value = false
  if (project) project.expanded = true
  saveActiveExecutionContext()
}
async function chooseChatWorkspace(type: 'directory' | 'file') {
  if (!isTauri()) { workspaceError.value = '工作区选择需要在 Lucas Agent 桌面程序中使用。'; return }
  try {
    const selection = await openNativeDialog({ directory: type === 'directory', multiple: false, title: type === 'directory' ? '选择会话工作目录' : '选择会话工作文件' })
    if (typeof selection === 'string') {
      selectedWorkspaceProjectId.value = null
      selectedWorkspacePath.value = selection
      selectedWorkspacePathType.value = type
      workspaceMenuOpen.value = false
      await saveActiveExecutionContext()
    }
  } catch (error) { workspaceError.value = `选择工作区失败：${String(error)}` }
}
async function saveActiveExecutionContext() {
  const chat = activeChat.value
  if (!chat) return
  try {
    await updateExecutionContext(chat.id, { projectId: selectedWorkspaceProjectId.value, workspacePath: selectedWorkspacePath.value, workspacePathType: selectedWorkspacePathType.value, permissionMode: permissionMode.value })
    chat.projectId = selectedWorkspaceProjectId.value
    chat.workspacePath = selectedWorkspacePath.value
    chat.workspacePathType = selectedWorkspacePathType.value
    chat.permissionMode = permissionMode.value
  } catch (error) { workspaceError.value = `保存工作区权限失败：${String(error)}` }
}
async function setPermissionMode(mode: 'approval' | 'fullAccess') {
  permissionMode.value = mode
  permissionMenuOpen.value = false
  await saveActiveExecutionContext()
}
function beginNewChat() {
  activeView.value = 'chat'
  activeChatId.value = ''
  selectedWorkspacePath.value = ''
  selectedWorkspacePathType.value = 'directory'
  selectedWorkspaceProjectId.value = null
  permissionMode.value = settings.value.requireToolApproval ? 'approval' : 'fullAccess'
  workspaceMenuOpen.value = false
  permissionMenuOpen.value = false
  prompt.value = ''
  usage.value = { inputTokens: 0, outputTokens: 0, estimated: true }
  openChatMenuId.value = null
  openProjectMenuId.value = null
  workspaceError.value = ''
}
function adjustOutputFontSize(delta: number) {
  outputFontSize.value = Math.min(22, Math.max(13, outputFontSize.value + delta))
  localStorage.setItem('lucas-output-font-size', String(outputFontSize.value))
}
async function focusSearch() {
  showSearch.value = true
  await nextTick()
  searchInput.value?.focus()
  searchInput.value?.select()
}
function findInConversation(backwards = false) {
  const query = searchText.value.trim()
  if (!query) return
  const findText = (window as Window & {
    find?: (text: string, caseSensitive?: boolean, backwards?: boolean, wrapAround?: boolean, wholeWord?: boolean, searchInFrames?: boolean, showDialog?: boolean) => boolean
  }).find
  if (!findText) {
    workspaceError.value = '当前运行环境不支持查找对话内容。'
    return
  }
  const found = findText.call(window, query, false, backwards, true, false, true, false)
  if (!found) findText.call(window, query, false, backwards, false, false, true, false)
}
function syncFullscreenState() {
  if (!isTauri()) isFullscreen.value = Boolean(document.fullscreenElement)
}
async function toggleFullscreen() {
  try {
    if (isTauri()) {
      const appWindow = getCurrentWindow()
      const nextState = !(await appWindow.isFullscreen())
      await appWindow.setFullscreen(nextState)
      isFullscreen.value = nextState
    } else if (document.fullscreenElement) {
      await document.exitFullscreen()
    } else {
      await document.documentElement.requestFullscreen()
    }
  } catch (error) { workspaceError.value = `切换全屏失败：${String(error)}` }
}
function handleSearchKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault()
    showSearch.value = false
    searchText.value = ''
  } else if (event.key === 'Enter') {
    event.preventDefault()
    findInConversation(event.shiftKey)
  }
}
async function assignChat(chatId: string, projectId: string | null) {
  const chat = chats.value.find(item => item.id === chatId)
  if (!chat) return
  try {
    await updateExecutionContext(chat.id, { projectId: selectedWorkspaceProjectId.value, workspacePath: selectedWorkspacePath.value, workspacePathType: selectedWorkspacePathType.value, permissionMode: permissionMode.value })
    chat.projectId = selectedWorkspaceProjectId.value
    chat.workspacePath = selectedWorkspacePath.value
    chat.workspacePathType = selectedWorkspacePathType.value
    chat.permissionMode = permissionMode.value
  } catch (error) {
    workspaceError.value = `保存工作区权限失败：${String(error)}`
    if (chat.messages.length === 0) { await deleteSession(chat.id); chats.value = chats.value.filter(item => item.id !== chat.id); activeChatId.value = '' }
    return
  }
  try {
    const linkedProject = projects.value.find(item => item.id === projectId)
    await updateExecutionContext(chatId, { projectId, workspacePath: linkedProject?.workspacePath ?? '', workspacePathType: linkedProject?.workspacePathType ?? 'directory', permissionMode: chat.permissionMode ?? 'approval' })
    chat.projectId = projectId
    chat.workspacePath = linkedProject?.workspacePath ?? ''
    chat.workspacePathType = linkedProject?.workspacePathType ?? 'directory'
    if (activeChatId.value === chatId) {
      const project = projects.value.find(item => item.id === projectId)
      selectedWorkspaceProjectId.value = projectId
      selectedWorkspacePath.value = project?.workspacePath ?? ''
      selectedWorkspacePathType.value = project?.workspacePathType ?? 'directory'
    }
  } catch (error) { workspaceError.value = `更新项目关联失败：${String(error)}` }
  openChatMenuId.value = null
  if (projectId) {
    const project = projects.value.find(item => item.id === projectId)
    if (project) project.expanded = true
  }
}
function requestDeleteChat(chat: Chat) {
  workspaceDialog.value = { kind: 'delete-chat', id: chat.id, name: chat.title }
  openChatMenuId.value = null
}
function requestDeleteProject(project: Project) {
  workspaceDialog.value = {
    kind: 'delete-project',
    id: project.id,
    name: project.name,
    relatedCount: chats.value.filter(chat => chat.projectId === project.id).length,
  }
  openProjectMenuId.value = null
}
function requestRenameChat(chat: Chat) {
  workspaceDialog.value = { kind: 'rename-chat', id: chat.id, name: chat.title }
  workspaceDialogName.value = chat.title
  openChatMenuId.value = null
}
function requestRenameProject(project: Project) {
  workspaceDialog.value = { kind: 'rename-project', id: project.id, name: project.name }
  workspaceDialogName.value = project.name
  openProjectMenuId.value = null
}
function closeWorkspaceDialog() {
  if (workspaceDialogBusy.value) return
  workspaceDialog.value = null
  workspaceDialogName.value = ''
}
async function confirmWorkspaceDialog() {
  const dialog = workspaceDialog.value
  if (!dialog) return
  workspaceDialogBusy.value = true
  try {
    if (dialog.kind === 'delete-chat') {
      await deleteSession(dialog.id)
      const index = chats.value.findIndex(item => item.id === dialog.id)
      chats.value = chats.value.filter(item => item.id !== dialog.id)
      if (activeChatId.value === dialog.id) activeChatId.value = chats.value[Math.max(0, index - 1)]?.id ?? ''
    } else if (dialog.kind === 'delete-project') {
      await deleteProjectRequest(dialog.id)
      projects.value = projects.value.filter(item => item.id !== dialog.id)
      chats.value.forEach(chat => { if (chat.projectId === dialog.id) chat.projectId = null })
    } else {
      const name = workspaceDialogName.value.trim()
      if (!name) return
      if (dialog.kind === 'rename-chat') {
        await renameSessionRequest(dialog.id, name)
        const chat = chats.value.find(item => item.id === dialog.id)
        if (chat) chat.title = name
      } else {
        await renameProjectRequest(dialog.id, name)
        const project = projects.value.find(item => item.id === dialog.id)
        if (project) project.name = name
      }
    }
    workspaceDialog.value = null
    workspaceDialogName.value = ''
  } catch (error) {
    workspaceError.value = `操作失败：${String(error)}`
    workspaceDialog.value = null
  } finally { workspaceDialogBusy.value = false }
}
function configureProvider(name: string) {
  const provider = providers.value.find(item => item.name === name)
  providerDraft.value = {
    existingName: name,
    name,
    baseUrl: provider?.baseUrl ?? '',
    models: provider?.models.length ? [...provider.models] : [''],
    apiKey: '',
  }
  providerHasKey.value = provider?.hasApiKey ?? false
  showProviderSettings.value = true
  showModelMenu.value = false
}
function addProviderConfiguration() {
  providerDraft.value = { existingName: '', name: '', baseUrl: '', models: [''], apiKey: '' }
  providerHasKey.value = false
  showProviderSettings.value = true
  showModelMenu.value = false
}
function isDeepSeekBaseUrl(value: string) {
  try { return new URL(value).hostname.toLowerCase() === 'api.deepseek.com' } catch { return false }
}
async function queryProviderBalance(name = providerDraft.value.existingName) {
  if (!name || providerBalanceLoading.value) return
  providerBalanceLoading.value = true
  providerBalanceError.value = ''
  providerBalance.value = null
  providerBalanceName.value = name
  try { providerBalance.value = await getProviderBalance(name) }
  catch (error) { providerBalanceError.value = String(error) }
  finally { providerBalanceLoading.value = false }
}
function addModelField() { providerDraft.value.models.push('') }
function removeModelField(index: number) {
  if (providerDraft.value.models.length > 1) providerDraft.value.models.splice(index, 1)
}
async function saveProviderConfiguration() {
  providerSaving.value = true
  try {
    const saved = await saveProvider({
      ...providerDraft.value,
      existingName: providerDraft.value.existingName || null,
      models: providerDraft.value.models.map(model => model.trim()).filter(Boolean),
    })
    providers.value = [...providers.value.filter(item => item.name !== providerDraft.value.existingName && item.name !== saved.name), saved]
    selectModel(saved.name, saved.models[0])
    showProviderSettings.value = false
    providerDraft.value.apiKey = ''
  } catch (error) { workspaceError.value = `保存模型配置失败：${String(error)}` }
  finally { providerSaving.value = false }
}
async function removeProviderConfiguration() {
  const name = providerDraft.value.existingName
  if (!name || !window.confirm(`确定删除服务商“${name}”及其下全部 AI 模型吗？`)) return
  try {
    await deleteProvider(name)
    providers.value = providers.value.filter(provider => provider.name !== name)
    if (selectedProviderName.value === name) {
      const nextProvider = providers.value.find(provider => provider.models.length > 0)
      selectModel(nextProvider?.name ?? '', nextProvider?.models[0] ?? '')
    }
    showProviderSettings.value = false
  } catch (error) { workspaceError.value = `删除服务商配置失败：${String(error)}` }
}
async function sendMessage() {
  const text = prompt.value.trim()
  if (!text || isRunning.value) return
  if (!selectedModel.value) {
    workspaceError.value = '请先在模型菜单中添加并选择一个 AI 模型。'
    return
  }
  const chat = activeChat.value ?? await createChat()
  if (!chat) return
  const previousUsage = getSessionUsage(chat)
  chat.messages.push({ id: crypto.randomUUID(), createdAt: new Date().toISOString(), role: 'user', content: text })
  if (chat.title === '新对话') chat.title = text.slice(0, 34)
  prompt.value = ''
  const assistant: Chat['messages'][number] = { id: crypto.randomUUID(), createdAt: new Date().toISOString(), role: 'assistant', content: '', reasoning: '', startedAtMs: Date.now(), toolStatus: '' }
  chat.messages.push(assistant)
  usage.value = { inputTokens: previousUsage.inputTokens + Math.max(1, Math.ceil(text.length * 0.65)), outputTokens: previousUsage.outputTokens, estimated: true }
  let hidingUnsupportedToolText = false
  let hidingUnsupportedToolReasoning = false
  isRunning.value = true
  runPaused.value = false
  runStopping.value = false
  runtimeTick.value = Date.now()
  if (runtimeTimer) clearInterval(runtimeTimer)
  runtimeTimer = setInterval(() => { runtimeTick.value = Date.now() }, 250)
  try {
    await startAgentRun(chat.id, text, selectedProviderName.value, selectedModel.value, (event: AgentEvent) => {
      if (event.type === 'run.started') assistant.startedAtMs = Date.now()
      if (event.type === 'run.started') assistant.toolStatus = '正在思考…'
      if (event.type === 'reasoning.delta') {
        if (!hidingUnsupportedToolReasoning) {
          const nextReasoning = assistant.reasoning + (event.text ?? '')
          if (containsUnsupportedToolProtocol(nextReasoning)) {
            hidingUnsupportedToolReasoning = true
            assistant.reasoning = '检测到模型在思考流中输出内部工具协议，已隐藏该过程内容。'
          } else assistant.reasoning = nextReasoning
        }
        assistant.toolStatus = '正在思考…'
      }
      if (event.type === 'reasoning.replace') {
        hidingUnsupportedToolReasoning = true
        assistant.reasoning = event.text ?? ''
      }
      if (event.type === 'message.delta') {
        if (hidingUnsupportedToolText) return
        const nextContent = assistant.content + (event.text ?? '')
        if (containsUnsupportedToolProtocol(nextContent)) {
          hidingUnsupportedToolText = true
          assistant.content = '检测到模型把内部工具协议当作正文输出，已隐藏这段内容。若没有后续回答，请改用支持标准 function calling 的模型或服务商后重试。'
          assistant.toolStatus = '已拦截非标准工具调用文本'
          usage.value.outputTokens = previousUsage.outputTokens + Math.max(1, Math.ceil(assistant.content.length * 0.65))
          return
        }
        assistant.content = nextContent
        assistant.toolStatus = '正在生成回答…'
        usage.value.outputTokens = previousUsage.outputTokens + Math.max(1, Math.ceil(assistant.content.length * 0.65))
      }
      if (event.type === 'message.replace') {
        hidingUnsupportedToolText = false
        assistant.content = event.text ?? ''
        usage.value.outputTokens = previousUsage.outputTokens + Math.max(1, Math.ceil(assistant.content.length * 0.65))
      }
      if (event.type === 'usage.update' && event.usage) {
        usage.value = { inputTokens: event.usage.inputTokens, outputTokens: event.usage.outputTokens, estimated: event.usage.estimated }
        assistant.inputTokens = Math.max(0, event.usage.inputTokens - previousUsage.inputTokens)
        assistant.outputTokens = Math.max(0, event.usage.outputTokens - previousUsage.outputTokens)
      }
      if (event.type === 'tool.approval_required' && event.requestId) { approvalRequest.value = { requestId: event.requestId, toolName: event.toolName ?? '本地工具', details: event.text ?? '' }; assistant.toolStatus = '等待用户批准…' }
      if (event.type === 'tool.started') assistant.toolStatus = `正在执行本地工具：${event.toolName ?? '工作区操作'}`
      if (event.type === 'tool.completed') assistant.toolStatus = '正在整理工具结果…'
      if (event.type === 'run.git_diff') assistant.gitDiff = event.text ?? ''
      if (event.type === 'run.completed') { assistant.elapsedMilliseconds = event.elapsedMilliseconds ?? Date.now() - (assistant.startedAtMs ?? Date.now()); assistant.toolStatus = '' }
      if (event.type === 'run.stopped') { assistant.elapsedMilliseconds = Date.now() - (assistant.startedAtMs ?? Date.now()); assistant.toolStatus = '已停止' }
    })
  } catch (error) {
    assistant.content += `\n\nAgent 运行失败：${String(error)}`
    assistant.elapsedMilliseconds ??= Date.now() - (assistant.startedAtMs ?? Date.now())
  }
  finally { isRunning.value = false; runPaused.value = false; runStopping.value = false; if (runtimeTimer) clearInterval(runtimeTimer); runtimeTimer = undefined; if (approvalRequest.value) approvalRequest.value = null }
}
function selectModel(providerName: string, model: string) {
  selectedProviderName.value = providerName
  selectedModel.value = model
  showModelMenu.value = false
}
function containsUnsupportedToolProtocol(content: string) {
  const normalized = content.replace(/[\s\u200B-\u200D\uFEFF]/g, '').replace(/｜/g, '|').replace(/＜/g, '<').replace(/＞/g, '>')
  return /<\|+(?:DSML|DMSL)\|+(?:CALLS|FUNCTION_CALLS|INVOKE)\b/i.test(normalized)
}
</script>

<template>
  <div class="app-shell">
    <aside class="sidebar">
      <div class="brand">Lucas Agent</div>
      <button class="new-chat" @click="beginNewChat"><IconMessagePlus :size="17" :stroke-width="1.8" /> <span>新对话</span></button>
      <nav class="nav-list">
        <button :class="{ selected: activeView === 'skills' }" @click="activeView = 'skills'"><IconSparkles /><span>技能</span></button>
        <button :class="{ selected: activeView === 'memories' }" @click="activeView = 'memories'"><IconBook2 /><span>记忆文件</span></button>
        <button :class="{ selected: activeView === 'settings' }" @click="activeView = 'settings'"><IconSettings /><span>设置</span></button>
      </nav>

      <section class="sidebar-section projects-section">
        <div class="section-heading"><span>项目</span><button class="section-add" aria-label="新建项目" title="新建项目" @click="isCreatingProject = true">＋</button></div>
        <div v-for="project in projects" :key="project.id" class="project-block">
          <div class="project-row-wrap">
            <button class="project-row" @click="project.expanded = !project.expanded; openProjectMenuId = null; openChatMenuId = null">
              <IconChevronRight class="project-chevron" :class="{ expanded: project.expanded }" :size="14" />
              <IconFolder class="project-icon" :size="16" :stroke-width="1.7" />
              <span class="project-name">{{ project.name }}</span>
            </button>
            <button class="project-menu-trigger" :aria-label="`管理项目 ${project.name}`" title="删除项目" @click.stop="openProjectMenuId = openProjectMenuId === project.id ? null : project.id"><IconDots :size="16" /></button>
            <div v-if="openProjectMenuId === project.id" class="project-menu"><button @click="requestRenameProject(project)"><IconPencil :size="15" />重命名</button><button class="delete-action" @click="requestDeleteProject(project)"><IconTrash :size="15" />删除项目</button></div>
          </div>
          <div v-if="project.expanded" class="project-chats">
            <div v-for="chat in chats.filter(item => item.projectId === project.id)" :key="chat.id" class="recent-wrap project-chat-wrap">
              <button class="recent-item project-chat" :class="{ active: activeChatId === chat.id }" @click="openChat(chat)">{{ chat.title }}</button>
              <button class="chat-menu-trigger" :aria-label="`管理 ${chat.title}`" title="解除项目关联" @click.stop="openChatMenuId = openChatMenuId === chat.id ? null : chat.id"><IconDots :size="16" /></button>
              <div v-if="openChatMenuId === chat.id" class="chat-assignment-menu"><div class="menu-caption">对话操作</div><button @click="requestRenameChat(chat)"><IconPencil :size="15" />重命名</button><button class="unassign-action" @click="assignChat(chat.id, null)">移回最近</button><button class="delete-action" @click="requestDeleteChat(chat)"><IconTrash :size="15" />删除对话</button></div>
            </div>
            <div v-if="!chats.some(item => item.projectId === project.id)" class="empty-project">暂无对话</div>
          </div>
        </div>
      </section>

      <section class="sidebar-section recent-section">
        <div class="section-heading"><span>最近</span></div>
        <div v-for="chat in unassignedChats" :key="chat.id" class="recent-wrap">
          <button class="recent-item" :class="{ active: activeChatId === chat.id }" @click="openChat(chat)">{{ chat.title }}</button>
          <button class="chat-menu-trigger" :aria-label="`管理 ${chat.title}`" title="关联到项目" @click.stop="openChatMenuId = openChatMenuId === chat.id ? null : chat.id"><IconDots :size="16" /></button>
          <div v-if="openChatMenuId === chat.id" class="chat-assignment-menu">
            <div class="menu-caption">移动到项目</div>
            <button v-for="project in projects" :key="project.id" @click="assignChat(chat.id, project.id)"><IconFolder :size="15" />{{ project.name }}</button>
            <div v-if="projects.length === 0" class="menu-empty">先创建一个项目</div>
            <button @click="requestRenameChat(chat)"><IconPencil :size="15" />重命名</button>
            <button class="delete-action" @click="requestDeleteChat(chat)"><IconTrash :size="15" />删除对话</button>
          </div>
        </div>
        <div v-if="unassignedChats.length === 0" class="empty-recent">没有未归类的对话</div>
      </section>

      <section v-if="sidebarBalanceProvider" class="sidebar-balance">
        <button class="sidebar-balance-query" :disabled="providerBalanceLoading" @click="queryProviderBalance(sidebarBalanceProvider.name)">
          <span class="sidebar-balance-title">DeepSeek 余额</span>
          <span class="sidebar-balance-action">{{ providerBalanceLoading && providerBalanceName === sidebarBalanceProvider.name ? '查询中…' : '查询余额' }}</span>
        </button>
        <template v-if="providerBalanceName === sidebarBalanceProvider.name && providerBalance">
          <div class="sidebar-balance-status" :class="{ unavailable: !providerBalance.isAvailable }">{{ providerBalance.isAvailable ? '账户可用于 API 调用' : '账户余额不足' }}</div>
          <div v-for="balance in providerBalance.balanceInfos" :key="balance.currency" class="sidebar-balance-amount"><strong>{{ balance.currency }} {{ balance.totalBalance }}</strong><small>赠金 {{ balance.grantedBalance }} · 充值 {{ balance.toppedUpBalance }}</small></div>
          <div v-if="!providerBalance.balanceInfos.length" class="sidebar-balance-status">接口未返回余额明细</div>
        </template>
        <div v-if="providerBalanceName === sidebarBalanceProvider.name && providerBalanceError" class="sidebar-balance-error">{{ providerBalanceError }}</div>
      </section>
    </aside>

    <main class="main-panel">
      <header v-if="activeView === 'chat'" class="topbar">
        <div class="header-title-block">
          <div class="window-title">{{ activeChat?.title || 'Lucas Agent' }}</div>
          <div v-if="projectContextLabel" class="conversation-project" :title="visibleProject?.workspacePath || selectedWorkspacePath">
            <IconFolder :size="13" /><span>{{ projectContextLabel }}</span>
          </div>
        </div>
        <div class="top-actions">
          <div class="font-controls" aria-label="输出字号">
            <button title="缩小输出字号" aria-label="缩小输出字号" :disabled="outputFontSize <= 13" @click="adjustOutputFontSize(-1)">A−</button>
            <span>{{ outputFontSize }}px</span>
            <button title="放大输出字号" aria-label="放大输出字号" :disabled="outputFontSize >= 22" @click="adjustOutputFontSize(1)">A<IconPlus :size="12" /></button>
          </div>
          <div v-if="showSearch" class="conversation-search">
            <IconSearch :size="15" />
            <input ref="searchInput" v-model="searchText" placeholder="查找对话内容" aria-label="查找对话内容" @keydown="handleSearchKeydown" />
            <button title="关闭查找" aria-label="关闭查找" @click="showSearch = false; searchText = ''"><IconX :size="15" /></button>
          </div>
          <button v-else class="top-action-button" title="查找对话内容" aria-label="查找对话内容" @click="focusSearch"><IconSearch :size="17" /></button>
          <button class="top-action-button" :title="isFullscreen ? '退出全屏' : '全屏'" :aria-label="isFullscreen ? '退出全屏' : '全屏'" @click="toggleFullscreen">
            <IconMinimize v-if="isFullscreen" :size="17" /><IconMaximize v-else :size="17" />
          </button>
        </div>
      </header>
      <div v-if="workspaceError" class="workspace-error" role="alert">{{ workspaceError }}<button @click="workspaceError = ''">关闭</button></div>
      <section v-if="activeView === 'chat' && activeChat" ref="conversationElement" class="conversation" :style="{ '--output-font-size': `${outputFontSize}px` }" @scroll="updateScrollToBottomVisibility">
        <div v-for="(message, index) in activeChat?.messages || []" :key="index" :class="['message', message.role]">
          <div v-if="message.role === 'assistant'" class="assistant-label">Lucas Agent</div>
          <details v-if="message.role === 'assistant' && message.reasoning && settings.showReasoning" class="reasoning-panel"><summary>思考过程</summary><div>{{ message.reasoning }}</div></details>
          <div v-if="message.role === 'assistant'" class="message-content markdown-content" v-html="renderMarkdown(message.content)"></div>
          <div v-if="message.role === 'assistant' && (message.startedAtMs || message.toolStatus || message.elapsedMilliseconds !== undefined)" class="assistant-run-meta"><span v-if="message.toolStatus">{{ message.toolStatus }}</span><span>{{ message.elapsedMilliseconds === undefined ? '已处理' : '已用时' }} {{ formatDuration(messageDuration(message)) }}</span></div>
          <div v-if="message.role !== 'assistant'" class="message-content">{{ message.content }}</div>
          <div v-if="message.role === 'assistant' && message.gitDiff !== undefined" class="git-review">
            <button class="git-review-trigger" @click="message.showGitDiff = !message.showGitDiff">{{ message.gitDiff ? '查看本轮代码改动' : '本轮没有 Git 改动' }}<IconChevronDown :class="{ open: message.showGitDiff }" :size="14" /></button>
            <div v-if="message.showGitDiff && message.gitDiff" class="git-diff-viewer"><div v-for="(line, lineIndex) in message.gitDiff.split('\n')" :key="lineIndex" class="git-diff-line" :class="{ added: line.startsWith('+') && !line.startsWith('+++'), removed: line.startsWith('-') && !line.startsWith('---'), hunk: line.startsWith('@@'), file: line.startsWith('diff --git') || line.startsWith('index ') }">{{ line || ' ' }}</div></div>
          </div>
        </div>
      </section>
      <section v-else-if="activeView === 'chat'" class="landing-page"><div class="landing-content"><div class="landing-icon"><IconSparkles :size="22" /></div><h1>你好，我是 Lucas Agent</h1><p>描述任务后，我可以在工作区中协助你。</p><span>输入第一条消息后，将自动创建一个新对话</span></div></section>
      <button v-if="activeView === 'chat' && activeChat && showScrollToBottom" class="scroll-bottom-button" aria-label="滚动到对话底部" title="滚动到对话底部" @click="scrollConversationToBottom"><IconChevronDown :size="20" :stroke-width="2" /></button>
      <section v-if="activeView === 'chat'" class="composer-wrap">
        <div class="composer">
          <textarea v-model="prompt" placeholder="随心输入" @keydown.enter.exact.prevent="!isRunning && sendMessage()" />
          <div class="composer-footer"><div class="composer-left">
            <div class="workspace-picker">
              <button class="plus workspace-plus" aria-label="选择工作区" title="选择工作区" :aria-expanded="workspaceMenuOpen" @click="workspaceMenuOpen = !workspaceMenuOpen; permissionMenuOpen = false">＋</button>
              <div v-if="workspaceMenuOpen" class="execution-menu workspace-menu">
                <div class="execution-menu-title">选择工作区</div>
                <button v-for="project in projects" :key="project.id" class="execution-menu-option" :class="{ selected: selectedWorkspaceProjectId === project.id }" @click="setProjectWorkspace(project)"><IconFolder :size="15" /><span class="workspace-option-text"><strong>{{ project.name }}</strong><small>{{ project.workspacePath }}</small></span></button>
                <div v-if="projects.length" class="execution-divider"></div>
                <button class="execution-menu-option" @click="chooseChatWorkspace('directory')"><IconFolder :size="15" /><span>选择工作目录…</span></button>
                <button class="execution-menu-option" @click="chooseChatWorkspace('file')"><IconBook2 :size="15" /><span>选择单个工作文件…</span></button>
                <button v-if="selectedWorkspacePath" class="execution-menu-option clear-workspace" @click="setProjectWorkspace(null)"><IconX :size="15" /><span>清除工作区</span></button>
                <p class="execution-menu-note">{{ selectedWorkspaceLabel }}</p>
              </div>
            </div>
            <div class="permission-picker">
              <button class="execution-trigger" :class="{ elevated: permissionMode === 'fullAccess' }" :aria-expanded="permissionMenuOpen" @click="permissionMenuOpen = !permissionMenuOpen; workspaceMenuOpen = false"><span class="permission-dot">●</span><span>{{ permissionMode === 'approval' ? '请求用户批准' : 'AI 完全访问' }}</span><IconChevronDown :size="13" /></button>
              <div v-if="permissionMenuOpen" class="execution-menu permission-menu">
                <div class="execution-menu-title">AI 权限</div>
                <button class="execution-menu-option" :class="{ selected: permissionMode === 'approval' }" @click="setPermissionMode('approval')"><span class="permission-radio"></span><span class="workspace-option-text"><strong>请求用户批准</strong><small>每次读取、写入或运行命令前都先确认</small></span></button>
                <button class="execution-menu-option" :class="{ selected: permissionMode === 'fullAccess' }" @click="setPermissionMode('fullAccess')"><span class="permission-radio"></span><span class="workspace-option-text"><strong>AI 完全访问</strong><small>在不逐项确认的情况下使用本地工具</small></span></button>
                <p class="execution-menu-note">工作区文件工具范围：{{ selectedWorkspaceLabel }}。注意：终端命令以当前系统用户权限运行，操作系统不保证将其限制在工作区内。</p>
              </div>
            </div>
          </div>
            <div class="composer-right"><span v-if="settings.showTokenUsage && (usage.inputTokens || usage.outputTokens)" class="token-usage">{{ usage.inputTokens }} 输入 · {{ usage.outputTokens }} 输出 token{{ usage.estimated ? '（估算）' : '' }}</span><div class="model-picker">
              <button class="model-button" :title="selectedProviderName" @click="showModelMenu = !showModelMenu">{{ modelPickerLabel }}<IconChevronDown class="model-chevron" :class="{ open: showModelMenu }" :size="14" :stroke-width="1.8" /></button>
              <div v-if="showModelMenu" class="model-menu">
                <div v-if="providers.length === 0" class="model-menu-empty">还没有添加 AI 服务商</div>
                <section v-for="provider in providers" :key="provider.name" class="model-provider-group">
                  <div class="model-provider-heading"><span>{{ provider.name }}</span><button class="provider-config-button" :title="`配置 ${provider.name}`" @click="configureProvider(provider.name)"><IconSettings :size="14" /></button></div>
                  <button v-for="model in provider.models" :key="model" class="model-option" :class="{ selected: selectedProviderName === provider.name && selectedModel === model }" @click="selectModel(provider.name, model)">{{ model }}</button>
                </section>
                <button class="add-provider-option" @click="addProviderConfiguration">＋ 添加服务商</button>
              </div>
            </div><button class="send" :class="{ paused: runPaused, stopping: runStopping }" :disabled="runStopping || (!isRunning && !selectedModel)" :title="isRunning ? runPaused ? '再次点击停止执行' : '点击暂停；暂停后再次点击停止' : '发送消息'" @click="isRunning ? pauseOrStop() : sendMessage()">{{ isRunning ? runPaused ? '■' : 'Ⅱ' : '↑' }}</button></div>
          </div>
        </div><div class="hint">{{ isRunning ? (runPaused ? '已暂停 · 再次点击停止执行' : '点击暂停；暂停在当前工具完成后生效，再次点击停止') : 'Enter 发送 · Shift + Enter 换行' }}</div>
      </section>

      <section v-if="activeView === 'skills'" class="management-page">
        <div class="management-heading"><div><h1>技能</h1><p>将可复用的工作指令保存到本机；启用的技能会随每次请求提供给模型。</p></div><button class="save-button" @click="newSkill">＋ 新建技能</button></div>
        <div v-if="skillDraft" class="editor-card"><h2>{{ skills.some(item => item.id === skillDraft?.id) ? '编辑技能' : '新建技能' }}</h2><label>名称<input v-model="skillDraft.name" maxlength="100" placeholder="例如：代码审查" /></label><label>说明<input v-model="skillDraft.description" maxlength="300" placeholder="这个技能适合什么任务" /></label><label>指令<textarea v-model="skillDraft.instructions" rows="7" placeholder="写下模型应遵循的具体步骤和约束" /></label><label class="toggle-row"><input v-model="skillDraft.enabled" type="checkbox" />启用并注入对话</label><div class="dialog-actions"><button class="cancel-button" @click="skillDraft = null">取消</button><button class="save-button" @click="persistSkill">保存技能</button></div></div>
        <div v-if="skills.length" class="management-list"><article v-for="skill in skills" :key="skill.id" class="management-card"><div><div class="card-title-row"><h2>{{ skill.name }}</h2><span class="state-tag" :class="{ enabled: skill.enabled }">{{ skill.enabled ? '已启用' : '已停用' }}</span></div><p>{{ skill.description || '暂无说明' }}</p><pre>{{ skill.instructions }}</pre></div><div class="card-actions"><button @click="editSkill(skill)">编辑</button><button class="delete-action" @click="removeSkill(skill)">删除</button></div></article></div><div v-else class="empty-management">还没有技能。可以创建一条常用工作流程，之后自动加入模型上下文。</div>
      </section>

      <section v-if="activeView === 'memories'" class="management-page">
        <div class="management-heading"><div><h1>记忆文件</h1><p>保存偏好、项目背景或长期约定；启用的内容会作为背景上下文发送给模型。</p></div><button class="save-button" @click="newMemory">＋ 新建记忆</button></div>
        <div v-if="memoryDraft" class="editor-card"><h2>{{ memories.some(item => item.id === memoryDraft?.id) ? '编辑记忆' : '新建记忆' }}</h2><label>标题<input v-model="memoryDraft.title" maxlength="100" placeholder="记忆标题" /></label><label>内容<textarea v-model="memoryDraft.content" rows="7" placeholder="输入希望 Agent 记住的内容" /></label><label class="toggle-row"><input v-model="memoryDraft.enabled" type="checkbox" />在对话中使用</label><div class="dialog-actions"><button class="cancel-button" @click="memoryDraft = null">取消</button><button class="save-button" @click="persistMemory">保存记忆</button></div></div>
        <div v-if="memories.length" class="management-list"><article v-for="memory in memories" :key="memory.id" class="management-card"><div><div class="card-title-row"><h2>{{ memory.title }}</h2><span class="state-tag" :class="{ enabled: memory.enabled }">{{ memory.enabled ? '使用中' : '已停用' }}</span></div><pre>{{ memory.content }}</pre></div><div class="card-actions"><button @click="editMemory(memory)">编辑</button><button class="delete-action" @click="removeMemory(memory)">删除</button></div></article></div><div v-else class="empty-management">还没有记忆。记忆保存在本机，不会跨设备同步。</div>
      </section>

      <section v-if="activeView === 'settings'" class="management-page settings-page">
        <div class="management-heading"><div><h1>设置</h1><p>本地偏好、运行安全与工作区校验参数。</p></div><button class="save-button" :disabled="settingsSaving" @click="persistSettings">{{ settingsSaving ? '保存中…' : '保存设置' }}</button></div>
        <div class="settings-group"><h2>显示</h2><label class="setting-row"><span><strong>显示思考过程</strong><small>仅展示模型接口实际返回的 reasoning 字段。</small></span><input v-model="settings.showReasoning" type="checkbox" /></label><label class="setting-row"><span><strong>显示 Token 用量</strong><small>实时估算或显示服务商返回的用量。</small></span><input v-model="settings.showTokenUsage" type="checkbox" /></label></div>
        <div class="settings-group"><h2>Harness Engineering 与 Loop Engineering</h2><p class="settings-note">Agent 根据任务自行选择工作区文件、工具和验证步骤，并把真实工具结果带回模型继续处理。单次运行最多进行 9 轮工具往返，避免意外无限循环。终端命令使用当前系统用户权限，工作目录不是操作系统级沙箱。</p><label class="setting-row"><span><strong>新对话默认请求批准</strong><small>设为默认安全模式；每个对话仍可在输入框权限菜单中单独切换。</small></span><input v-model="settings.requireToolApproval" type="checkbox" /></label></div>
        <div class="local-note">此应用为纯本地模式：没有账号登录或云端同步。SQLite 数据库位于当前系统用户的应用数据目录。</div>
      </section>
    </main>
    <div v-if="showProviderSettings" class="modal-backdrop" @click.self="showProviderSettings = false">
      <form class="provider-dialog" @submit.prevent="saveProviderConfiguration">
        <div class="dialog-heading"><div><h2>{{ providerDraft.existingName ? '编辑服务商' : '添加 AI 服务商' }}</h2><p>配置名称、OpenAI 兼容接口和该厂商提供的模型</p></div><button type="button" class="dialog-close" aria-label="关闭" @click="showProviderSettings = false"><IconX :size="18" /></button></div>
        <label>服务商配置名称<input v-model="providerDraft.name" required maxlength="80" placeholder="例如 DeepSeek、硅基流动" /></label>
        <label>Base URL<input v-model="providerDraft.baseUrl" type="url" required placeholder="https://api.example.com/v1" /></label>
        <div class="models-editor"><div class="models-editor-heading"><span>AI 模型</span><button type="button" class="add-model-button" @click="addModelField">＋ 添加模型</button></div>
          <div v-for="(model, index) in providerDraft.models" :key="index" class="model-input-row"><input v-model="providerDraft.models[index]" required :placeholder="index === 0 ? '例如 deepseek-chat' : '模型 ID'" /><button v-if="providerDraft.models.length > 1" type="button" :aria-label="`移除模型 ${index + 1}`" @click="removeModelField(index)"><IconX :size="16" /></button></div>
        </div>
        <label>API Key<input v-model="providerDraft.apiKey" type="password" :placeholder="providerHasKey ? '已保存密钥；留空则保持不变' : '粘贴 API Key'" autocomplete="new-password" /></label>
        <p class="secret-note">API Key 会在本机加密保存，不会回传给前端读取。Base URL 必须使用 HTTPS；仅 localhost 可使用 HTTP。</p>
        <section v-if="providerDraft.existingName && isDeepSeekBaseUrl(providerDraft.baseUrl)" class="provider-balance-panel">
          <div class="provider-balance-heading"><strong>DeepSeek 账户余额</strong><button type="button" class="add-model-button" :disabled="providerBalanceLoading || !providerHasKey" @click="queryProviderBalance(providerDraft.existingName)">{{ providerBalanceLoading && providerBalanceName === providerDraft.existingName ? '查询中…' : '查询余额' }}</button></div>
          <p v-if="!providerHasKey" class="secret-note">请先保存 API Key 后再查询。</p>
          <p v-if="providerBalanceName === providerDraft.existingName && providerBalanceError" class="provider-balance-error">{{ providerBalanceError }}</p>
          <template v-if="providerBalanceName === providerDraft.existingName && providerBalance"><p class="provider-balance-status">{{ providerBalance.isAvailable ? '账户当前可用于 API 调用' : '账户余额不足，当前无法调用 API' }}</p><div v-for="balance in providerBalance.balanceInfos" :key="balance.currency" class="provider-balance-row"><strong>{{ balance.currency }} {{ balance.totalBalance }}</strong><span>赠金 {{ balance.grantedBalance }} · 充值 {{ balance.toppedUpBalance }}</span></div><p v-if="!providerBalance.balanceInfos.length" class="secret-note">接口未返回余额明细。</p></template>
        </section>
        <div class="dialog-actions"><button v-if="providerDraft.existingName" type="button" class="reset-button" @click="removeProviderConfiguration">删除服务商</button><button type="button" class="cancel-button" @click="showProviderSettings = false">取消</button><button type="submit" class="save-button" :disabled="providerSaving">{{ providerSaving ? '保存中…' : '保存配置' }}</button></div>
      </form>
    </div>
    <div v-if="workspaceDialog" class="modal-backdrop" @click.self="closeWorkspaceDialog">
      <form v-if="workspaceDialog.kind === 'rename-chat' || workspaceDialog.kind === 'rename-project'" class="workspace-dialog" @submit.prevent="confirmWorkspaceDialog">
        <div class="dialog-heading"><div><h2>重命名{{ workspaceDialog.kind === 'rename-chat' ? '对话' : '项目' }}</h2><p>新的名称会显示在侧边栏中。</p></div><button type="button" class="dialog-close" aria-label="关闭" @click="closeWorkspaceDialog"><IconX :size="18" /></button></div>
        <label>{{ workspaceDialog.kind === 'rename-chat' ? '对话名称' : '项目名称' }}<input v-model="workspaceDialogName" autofocus required maxlength="100" /></label>
        <div class="dialog-actions"><button type="button" class="cancel-button" @click="closeWorkspaceDialog">取消</button><button type="submit" class="save-button" :disabled="workspaceDialogBusy">{{ workspaceDialogBusy ? '保存中…' : '保存名称' }}</button></div>
      </form>
      <section v-else class="workspace-dialog confirm-dialog" role="alertdialog" aria-modal="true">
        <div class="dialog-heading"><div><h2>{{ workspaceDialog.kind === 'delete-chat' ? '删除这条对话？' : '删除这个项目？' }}</h2><p>{{ workspaceDialog.name }}</p></div><button class="dialog-close" aria-label="关闭" @click="closeWorkspaceDialog"><IconX :size="18" /></button></div>
        <p v-if="workspaceDialog.kind === 'delete-chat'" class="confirm-description">该对话及其中的消息记录将被永久删除，无法恢复。</p>
        <p v-else class="confirm-description">项目将被删除；其中 {{ workspaceDialog.relatedCount }} 个对话会保留，并移回“最近”。</p>
        <div class="dialog-actions"><button class="cancel-button" :disabled="workspaceDialogBusy" @click="closeWorkspaceDialog">取消</button><button class="danger-button" :disabled="workspaceDialogBusy" @click="confirmWorkspaceDialog">{{ workspaceDialogBusy ? '处理中…' : workspaceDialog.kind === 'delete-chat' ? '删除对话' : '删除项目' }}</button></div>
      </section>
    </div>
    <div v-if="isCreatingProject" class="modal-backdrop" @click.self="isCreatingProject = false; projectName = ''; projectPath = ''">
      <form class="project-dialog" @submit.prevent="createProject">
        <div class="dialog-heading"><div><h2>创建项目</h2><p>为工作区创建一个便于识别的项目。</p></div><button type="button" class="dialog-close" aria-label="关闭" @click="isCreatingProject = false; projectName = ''; projectPath = ''"><IconX :size="18" /></button></div>
        <label class="project-name-field"><IconFolder :size="17" /><input v-model="projectName" autofocus required maxlength="100" placeholder="项目名称" /></label>
        <label class="project-source-label">源文件夹</label>
        <div class="project-source-picker">
          <select v-model="projectPathType" aria-label="源类型" @change="projectPath = ''"><option value="directory">在此电脑上添加文件夹</option><option value="file">添加单个工作文件</option></select>
          <button type="button" class="project-add-path" @click="chooseProjectWorkspace"><IconFolder :size="16" />{{ projectPath ? '更改路径' : '添加' }}</button>
          <div v-if="projectPath" class="project-selected-path" :title="projectPath"><IconFolder :size="15" /><span>{{ projectPath }}</span><button type="button" aria-label="移除路径" @click="projectPath = ''"><IconX :size="15" /></button></div>
        </div>
        <div class="project-dialog-actions"><button type="button" class="cancel-button" @click="isCreatingProject = false; projectName = ''; projectPath = ''">取消</button><button type="submit" class="save-button" :disabled="!projectName.trim() || !projectPath">创建项目</button></div>
      </form>
    </div>
    <div v-if="approvalRequest" class="modal-backdrop approval-backdrop">
      <section class="workspace-dialog approval-dialog" role="alertdialog" aria-modal="true" aria-labelledby="tool-approval-title">
        <div class="dialog-heading"><div><h2 id="tool-approval-title">允许 Agent 执行本地操作？</h2><p>{{ approvalRequest.toolName }}</p></div></div>
        <pre class="approval-details">{{ approvalRequest.details }}</pre>
        <p class="confirm-description">请检查操作内容。拒绝后，Agent 会收到拒绝结果并停止尝试该操作。</p>
        <div class="dialog-actions"><button class="cancel-button" :disabled="approvalBusy || runStopping" @click="decideToolApproval(false)">拒绝本次操作</button><button v-if="isRunning" class="cancel-button" :disabled="runStopping" @click="pauseOrStop">{{ runPaused ? '停止任务' : '暂停任务' }}</button><button class="save-button" :disabled="approvalBusy" @click="decideToolApproval(true)">{{ approvalBusy ? '提交中…' : '批准并执行' }}</button></div>
      </section>
    </div>
  </div>
</template>
