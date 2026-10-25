<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount, ref } from 'vue'
import MockWorkspace from './components/MockWorkspace.vue'
import { recordMock } from './mock-utils'
import type { MockRuleDraft, MockServer } from './mock-utils'
import type { Auth, Collection, Environment, ExecuteResult, Folder, History, RequestItem, Row, Workspace, UploadFile, UserInfo, ShareSummary, SharedItem } from './types'
import { newAuth, newRequest, newRow, uid } from './types'
import { curlCommand, folderPath, parseImport, resolveAuth, workspaceChanges } from './workspace-utils'
import { readGuestWorkspace, saveGuestWorkspace, readGuestHistory, addGuestHistory, importedGuestRevision, markGuestImported } from './browser-store'

const workspace = ref<Workspace>({ collections: [], environments: [], history: [] })
const selectedCollectionId = ref('')
const selectedRequestId = ref('')
const originalCollectionId = ref('')
const draft = ref<RequestItem>(newRequest())
const activeTab = ref<'params' | 'auth' | 'headers' | 'cookies' | 'body'>('params')
const openHeaderPickerId = ref('')
const responseTab = ref<'body' | 'headers' | 'cookies' | 'trace'>('body')
const sidebarTab = ref<'collections' | 'history' | 'shares' | 'mocks'>('collections')
const sidebarCollapsed = ref(false)
const mockWorkspace = ref<{ canLeave: () => boolean; hasUnsaved: () => boolean } | null>(null)
const mockRecording = ref<MockRuleDraft | null>(null)
const mockTargetServerId = ref('')
const showMockDestination = ref(false)
const mockDestinationMode = ref<'' | 'new' | 'existing'>('')
const mockDestinationId = ref('')
const mockDestinationServers = ref<MockServer[]>([])
const mockDestinationLoading = ref(false)
const mockDestinationError = ref('')
const pendingMockRecording = ref<MockRuleDraft | null>(null)
let mockDestinationOwner = ''
let mockDestinationGeneration = 0
function switchSidebar(value: typeof sidebarTab.value) {
  if (sidebarTab.value === 'mocks' && value !== 'mocks' && mockWorkspace.value?.canLeave() === false) return
  sidebarTab.value = value
}
async function prepareMock() {
  if (!response.value) return
  if (!currentUser.value) { flash('请先登录，再从响应区创建模拟接口'); openAuth(); return }
  try {
    pendingMockRecording.value = recordMock(copy(response.value))
    mockDestinationOwner = storageOwner(); mockDestinationMode.value = ''; mockDestinationId.value = ''
    mockDestinationServers.value = []; showMockDestination.value = true
    await loadMockDestinations()
  }
  catch (ex) { error.value = ex instanceof Error ? ex.message : '无法录制响应' }
}
async function loadMockDestinations() {
  const generation = ++mockDestinationGeneration
  mockDestinationLoading.value = true; mockDestinationError.value = ''
  try {
    const result = await accountFetch('/api/mocks', {}, mockDestinationOwner)
    const data = await result.json()
    if (generation !== mockDestinationGeneration || !showMockDestination.value) return
    if (mockDestinationOwner !== storageOwner()) throw new Error('账户已切换，请关闭窗口后重新创建模拟接口')
    if (!result.ok) throw new Error(data.error ?? '无法加载已有模拟服务')
    mockDestinationServers.value = data
  } catch (ex) {
    if (generation === mockDestinationGeneration && showMockDestination.value) mockDestinationError.value = ex instanceof Error ? ex.message : '无法加载已有模拟服务'
  } finally { if (generation === mockDestinationGeneration) mockDestinationLoading.value = false }
}
function cancelMockDestination() {
  showMockDestination.value = false; pendingMockRecording.value = null
  mockDestinationGeneration++; mockDestinationLoading.value = false
}
function confirmMockDestination() {
  if (!pendingMockRecording.value || !mockDestinationMode.value) return
  if (mockDestinationOwner !== storageOwner()) { mockDestinationError.value = '账户已切换，请关闭窗口后重新创建模拟接口'; return }
  if (mockDestinationMode.value === 'existing' && (mockDestinationLoading.value || !mockDestinationServers.value.some(x => x.id === mockDestinationId.value))) return
  mockRecording.value = copy(pendingMockRecording.value)
  mockTargetServerId.value = mockDestinationMode.value === 'existing' ? mockDestinationId.value : ''
  cancelMockDestination(); sidebarTab.value = 'mocks'
}
const folderCreationCollectionId = ref('')
const newFolderName = ref('')
const activeEnvironmentId = ref('')
const showEnvironments = ref(false)
const authScope = ref<'request' | 'folder' | 'collection'>('request')
const response = ref<ExecuteResult | null>(null)
const error = ref('')
const notice = ref('')
const busy = ref(false)
const saving = ref(false)
const files = ref<UploadFile[]>([])
const replayVariables = ref<Record<string, string> | null>(null)
let requestController: AbortController | null = null
let noticeTimer: ReturnType<typeof setTimeout> | undefined
let saveQueue: Promise<boolean> = Promise.resolve(true)
let pendingSaves = 0

const selectedCollection = computed(() => workspace.value.collections.find(x => x.id === selectedCollectionId.value))
const selectedFolder = computed(() => selectedCollection.value?.folders.find(x => x.id === draft.value.folderId))
const activeEnvironment = computed(() => workspace.value.environments.find(x => x.id === activeEnvironmentId.value))
const activeAuth = computed<Auth>(() => {
  if (authScope.value === 'collection') return selectedCollection.value?.auth ?? draft.value.auth
  if (authScope.value === 'folder') return selectedFolder.value?.auth ?? draft.value.auth
  return draft.value.auth
})
const effectiveAuth = computed<Auth>(() => resolveAuth(draft.value, selectedCollection.value))
const variableMap = computed(() => replayVariables.value ?? Object.fromEntries((activeEnvironment.value?.variables ?? []).filter(x => x.enabled && x.key).map(x => [x.key, x.value])))
const formattedBody = computed(() => {
  if (!response.value) return ''
  if (response.value.contentType.includes('json')) {
    try { return JSON.stringify(JSON.parse(response.value.body), null, 2) } catch { /* Keep raw response. */ }
  }
  return response.value.body
})
const methodClass = (method: string) => `method-${method.toLowerCase()}`
const copy = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T
const defaultHeaders = [{ key: 'Accept', value: '*/*' }, { key: 'User-Agent', value: 'WebPostman/1.0' }, { key: 'Accept-Encoding', value: 'gzip, deflate, br' }]
const commonHeaderNames = [
  'Accept', 'Accept-Encoding', 'Accept-Language', 'Authorization', 'Cache-Control',
  'Content-Type', 'Cookie', 'If-Match', 'If-None-Match', 'Origin', 'Pragma',
  'Referer', 'User-Agent', 'X-API-Key', 'X-Correlation-ID', 'X-Request-ID', 'X-Requested-With',
]
const matchingHeaderNames = (value: string) => commonHeaderNames.filter(name => name.toLowerCase().includes(value.trim().toLowerCase()))

function closeHeaderPicker(event: FocusEvent, rowId: string) {
  if (!(event.currentTarget as HTMLElement).contains(event.relatedTarget as Node | null) && openHeaderPickerId.value === rowId)
    openHeaderPickerId.value = ''
}

function chooseHeaderName(row: Row, name: string) {
  row.key = name
  openHeaderPickerId.value = ''
}

function focusHeaderOption(rowId: string, index: number) {
  const options = document.querySelectorAll<HTMLButtonElement>(`#header-options-${CSS.escape(rowId)} button`)
  if (index < 0) document.getElementById(`header-key-${rowId}`)?.focus()
  else options[Math.min(index, options.length - 1)]?.focus()
}

function flash(message: string) {
  notice.value = message
  clearTimeout(noticeTimer)
  noticeTimer = setTimeout(() => { notice.value = '' }, 2800)
}

function cancelRequest() { requestController?.abort() }

type EditorTab = { id: string; draft: RequestItem; collectionId: string; originalCollectionId: string; requestId: string; response: ExecuteResult | null; error: string; files: UploadFile[]; variables: Record<string, string> | null; baseline: string; busy: boolean; controller: AbortController | null }
const editorTabs = ref<EditorTab[]>([])
const activeEditorId = ref('')
const baseline = ref('')
const search = ref('')
const collapsedCollections = ref<string[]>([])
const collapsedFolders = ref<string[]>([])
const responseSearch = ref('')
const conflict = ref(false)
const saveError = ref('')
const historyTotal = ref(0)
const currentUser = ref<UserInfo | null>(null)
const workspaceLoading = ref(true)
const showAuth = ref(false)
const authMode = ref<'login' | 'register'>('login')
const authEmail = ref('')
const authPassword = ref('')
const authConfirmPassword = ref('')
const authBusy = ref(false)
const authError = ref('')
const guestAvailable = ref(false)
const guestImportBusy = ref(false)
const sessionChanged = ref(false)
let storageGeneration = 0
const shares = ref<ShareSummary[]>([])
const shareTotal = ref(0)
const shareQuery = ref('')
const shareKind = ref('')
const shareMine = ref(false)
const sharesBusy = ref(false)
const sharesError = ref('')
const shareDetail = ref<SharedItem | null>(null)
const shareDestination = ref('')
const showShare = ref(false)
const shareDraft = ref({ kind: 'request', collectionId: '', requestId: '', title: '', description: '', revision: -1 })
const sharePreview = ref<Collection | null>(null)
const shareBusy = ref(false)
const shareError = ref('')
let sharesGeneration = 0
let detailGeneration = 0
const storageOwner = () => currentUser.value?.id ?? 'guest'
async function accountFetch(url: string, options: RequestInit = {}, owner = storageOwner()) {
  const result = await fetch(url, { ...options, headers: { ...Object.fromEntries(new Headers(options.headers).entries()), 'X-WebPostman-User': owner } })
  if (result.status === 401 && currentUser.value) { authMode.value = 'login'; authEmail.value = currentUser.value.email; authError.value = '登录已失效，请重新登录同一账户，当前草稿已保留'; showAuth.value = true }
  return result
}

const savedWorkspace = ref<Workspace>({ collections: [], environments: [], history: [], revision: 0 })
const editorSignature = () => JSON.stringify([draft.value, selectedCollectionId.value])
const dirty = computed(() => editorSignature() !== baseline.value)
const workspaceDirty = computed(() => JSON.stringify(workspace.value.collections) !== JSON.stringify(savedWorkspace.value.collections) || JSON.stringify(workspace.value.environments) !== JSON.stringify(savedWorkspace.value.environments))
const responsePieces = computed(() => {
  const value = formattedBody.value; const term = responseSearch.value
  if (!term) return [{ text: value, match: false }]
  const pieces: { text: string; match: boolean }[] = []; let at = 0; let count = 0
  const lower = value.toLowerCase(); const query = term.toLowerCase()
  for (let next = lower.indexOf(query); next >= 0 && count < 1000; next = lower.indexOf(query, at)) {
    pieces.push({ text: value.slice(at, next), match: false }, { text: value.slice(next, next + term.length), match: true }); at = next + term.length; count++
  }
  pieces.push({ text: value.slice(at), match: false }); return pieces
})
const responseMatchCount = computed(() => responsePieces.value.filter(x => x.match).length)
function stashEditor() {
  const tab = editorTabs.value.find(x => x.id === activeEditorId.value)
  if (tab) Object.assign(tab, { draft: draft.value, collectionId: selectedCollectionId.value, originalCollectionId: originalCollectionId.value, requestId: selectedRequestId.value, response: response.value, error: error.value, files: files.value, variables: replayVariables.value, baseline: baseline.value, busy: busy.value, controller: requestController })
}
function activateEditor(tab: EditorTab) {
  if (tab.id === activeEditorId.value) return
  stashEditor(); activeEditorId.value = tab.id; draft.value = tab.draft
  selectedCollectionId.value = tab.collectionId; originalCollectionId.value = tab.originalCollectionId; selectedRequestId.value = tab.requestId
  response.value = tab.response; error.value = tab.error; files.value = tab.files; replayVariables.value = tab.variables
  baseline.value = tab.baseline; busy.value = tab.busy; requestController = tab.controller; authScope.value = 'request'; responseSearch.value = ''
}
function addEditor(request: RequestItem, collectionId: string, saved = false, variables: Record<string, string> | null = null) {
  const normalized = { ...newRequest(), ...copy(request) }
  delete normalized.files
  normalized.cookies ??= [newRow()]
  const tab: EditorTab = { id: uid(), draft: normalized, collectionId, originalCollectionId: saved ? collectionId : '', requestId: saved ? normalized.id : '', response: null, error: '', files: [], variables, baseline: JSON.stringify([normalized, collectionId]), busy: false, controller: null }
  editorTabs.value.push(tab); activateEditor(editorTabs.value[editorTabs.value.length - 1])
}
function tabDirty(tab: EditorTab) { return tab.id === activeEditorId.value ? dirty.value : JSON.stringify([tab.draft, tab.collectionId]) !== tab.baseline }
function closeEditor(tab: EditorTab) {
  if (tabDirty(tab) && !confirm('此标签有未保存修改，仍要关闭？')) return
  if (tab.id === activeEditorId.value) requestController?.abort(); else tab.controller?.abort()
  editorTabs.value = editorTabs.value.filter(x => x.id !== tab.id)
  if (activeEditorId.value === tab.id) { activeEditorId.value = ''; if (editorTabs.value[0]) activateEditor(editorTabs.value[0]); else createRequest(selectedCollectionId.value) }
}
function toggle(list: string[], id: string) { const index = list.indexOf(id); if (index < 0) list.push(id); else list.splice(index, 1) }
function matchesRequest(request: RequestItem) { const term = search.value.trim().toLowerCase(); return !term || `${request.name} ${request.url} ${request.method}`.toLowerCase().includes(term) }
function matchesCollection(collection: Collection) { const term = search.value.trim().toLowerCase(); return !term || collection.name.toLowerCase().includes(term) || collection.folders.some(f => f.name.toLowerCase().includes(term)) || collection.requests.some(matchesRequest) }
function shownRequests(collection: Collection, folderId = '') {
  const collectionMatch = collection.name.toLowerCase().includes(search.value.trim().toLowerCase())
  const folder = collection.folders.find(x => x.id === folderId)
  const folderMatch = folder && folderPath(collection, folder).toLowerCase().includes(search.value.trim().toLowerCase())
  return collection.requests.filter(x => x.folderId === folderId && (collectionMatch || folderMatch || matchesRequest(x)))
}
function folderRows(collection: Collection) {
  const rows: { folder: Folder; depth: number }[] = []
  const visit = (parentId = '', depth = 0) => {
    if (depth > 30) return
    for (const folder of collection.folders.filter(x => (x.parentId ?? '') === parentId)) {
      const path = folderPath(collection, folder)
      if (search.value && !collection.name.toLowerCase().includes(search.value.toLowerCase()) && !path.toLowerCase().includes(search.value.toLowerCase()) && !collection.requests.some(x => (x.folderId === folder.id || folderPath(collection, collection.folders.find(f => f.id === x.folderId) ?? folder).startsWith(path + ' / ')) && matchesRequest(x))) continue
      rows.push({ folder, depth }); if (search.value || !collapsedFolders.value.includes(folder.id)) visit(folder.id, depth + 1)
    }
  }; visit(); return rows
}
function persist(): Promise<boolean> {
  const owner = storageOwner(); const generation = storageGeneration
  const snapshot: Workspace = { collections: copy(workspace.value.collections), environments: copy(workspace.value.environments), history: [], revision: workspace.value.revision }; pendingSaves++; saving.value = true
  saveQueue = saveQueue.then(async () => {
    try {
      if (generation !== storageGeneration || owner !== storageOwner()) throw new Error('账户已切换，此次保存已取消')
      if (sessionChanged.value) throw new Error('账户已在其他页面切换，请先导出备份再重新载入')
      if (conflict.value) throw new Error('请先处理工作区冲突，未保存修改已保留')
      const changes = workspaceChanges(savedWorkspace.value, snapshot)
      if (!changes.collections.length && !changes.environments.length && !changes.deletedCollections.length && !changes.deletedEnvironments.length) return true
      let revision: number
      if (!currentUser.value) {
        try { revision = await saveGuestWorkspace(snapshot, savedWorkspace.value.revision ?? 0) }
        catch (ex) { if (ex instanceof Error && ex.message.includes('其他页面')) conflict.value = true; throw ex }
      } else {
        const result = await accountFetch('/api/workspace', { method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(changes) }, owner)
        const data = await result.json()
        if (result.status === 409) { conflict.value = true; sessionChanged.value = data.code === 'account_changed' }
        if (!result.ok) throw new Error(data.error ?? `保存失败 (${result.status})`)
        revision = data.revision
      }
      snapshot.revision = revision; savedWorkspace.value = snapshot; workspace.value.revision = revision; saveError.value = ''; return true
    } catch (ex) { saveError.value = ex instanceof Error ? ex.message : '保存失败'; return false }
    finally { pendingSaves--; saving.value = pendingSaves > 0 }
  }); return saveQueue
}
async function loadHistory(append = false) {
  const generation = storageGeneration
  if (!currentUser.value) {
    const history = await readGuestHistory(); if (generation !== storageGeneration) return
    historyTotal.value = history.length; workspace.value.history = history.slice(0, append ? workspace.value.history.length + 50 : 50); return
  }
  const result = await accountFetch(`/api/history?offset=${append ? workspace.value.history.length : 0}&limit=50`)
  if (!result.ok) throw new Error('加载历史记录失败')
  const page = await result.json(); if (generation !== storageGeneration) return
  workspace.value.history = append ? [...workspace.value.history, ...page.items] : page.items; historyTotal.value = page.total
}
async function loadWorkspace() {
  workspaceLoading.value = true
  try {
    const generation = ++storageGeneration
    for (const tab of editorTabs.value) tab.controller?.abort()
    requestController?.abort(); editorTabs.value = []; activeEditorId.value = ''; busy.value = false; requestController = null
    let loaded: Workspace
    if (currentUser.value) {
      const result = await accountFetch('/api/workspace'); if (!result.ok) throw new Error('无法加载账户工作区，请重新登录')
      loaded = await result.json()
    } else loaded = await readGuestWorkspace()
    if (generation !== storageGeneration) return
    workspace.value = loaded; savedWorkspace.value = copy(loaded); conflict.value = false; sessionChanged.value = false; saveError.value = ''
    activeEnvironmentId.value = ''; showEnvironments.value = false; shareDetail.value = null; detailGeneration++; mockRecording.value = null; cancelMockDestination()
    const collection = loaded.collections[0]
    if (collection?.requests[0]) openRequest(collection, collection.requests[0]); else addEditor(newRequest(), collection?.id ?? '')
    await loadHistory()
    try { const guest = await readGuestWorkspace(); const imported = currentUser.value ? await importedGuestRevision(currentUser.value.id) : undefined; guestAvailable.value = !!currentUser.value && (guest.collections.length > 0 || guest.environments.length > 0) && imported !== (guest.revision ?? 0) } catch { guestAvailable.value = false }
  } finally { workspaceLoading.value = false }
}
async function reloadWorkspace() {
  if (sidebarTab.value === 'mocks' && mockWorkspace.value?.canLeave() === false) return
  if ((workspaceDirty.value || editorTabs.value.some(tabDirty)) && !confirm('重新载入会丢弃未保存修改。请先导出备份，是否继续？')) return
  try {
    await saveQueue
    workspaceLoading.value = true
    const result = await fetch('/api/auth/me'); if (!result.ok) throw new Error('无法连接后端服务')
    currentUser.value = (await result.json()).user; await loadWorkspace()
  } catch (ex) {
    if (!currentUser.value) { try { await loadWorkspace() } catch { if (!editorTabs.value.length) addEditor(newRequest(), '') } }
    saveError.value = ex instanceof Error ? ex.message : '加载失败'
  } finally { workspaceLoading.value = false }
}
async function preserveDraftsBeforeSwitch() {
  if (sidebarTab.value === 'mocks' && mockWorkspace.value?.canLeave() === false) throw new Error('已取消账户切换，模拟草稿仍保留')
  await saveQueue; stashEditor()
  for (const tab of editorTabs.value.filter(tabDirty)) {
    let collection = workspace.value.collections.find(x => x.id === tab.collectionId)
    if (!collection) { collection = { id: uid(), name: '我的草稿', auth: newAuth(), folders: [], requests: [] }; workspace.value.collections.push(collection) }
    const request = copy(tab.draft); delete request.files
    if (tab.originalCollectionId && tab.originalCollectionId !== collection.id) { const previous = workspace.value.collections.find(x => x.id === tab.originalCollectionId); if (previous) previous.requests = previous.requests.filter(x => x.id !== request.id) }
    if (!collection.folders.some(x => x.id === request.folderId)) request.folderId = ''
    const index = collection.requests.findIndex(x => x.id === request.id)
    if (index >= 0) collection.requests[index] = request; else collection.requests.push(request)
    tab.collectionId = collection.id; tab.originalCollectionId = collection.id; tab.requestId = request.id; tab.draft = request
    if (tab.id === activeEditorId.value) { selectedCollectionId.value = collection.id; originalCollectionId.value = collection.id; selectedRequestId.value = request.id; draft.value = request }
  }
  if (workspaceDirty.value && !await persist()) throw new Error('切换前保存失败，请先导出备份，或重新登录同一邮箱继续保存')
  for (const tab of editorTabs.value) { tab.baseline = JSON.stringify([tab.draft, tab.collectionId]); if (tab.id === activeEditorId.value) baseline.value = tab.baseline }
}
async function submitAuth() {
  if (authBusy.value) return
  authBusy.value = true; authError.value = ''
  try {
    if (authMode.value === 'register' && authPassword.value !== authConfirmPassword.value) throw new Error('两次输入的密码不一致')
    if (!currentUser.value || currentUser.value.email !== authEmail.value.trim().toLowerCase()) await preserveDraftsBeforeSwitch()
    const result = await fetch(`/api/auth/${authMode.value}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: authEmail.value.trim(), password: authPassword.value, confirmPassword: authConfirmPassword.value }) })
    if (result.status === 429) throw new Error('操作过于频繁，请稍后再试')
    const data = await result.json(); if (!result.ok) throw new Error(data.error ?? '登录失败')
    const sameUser = currentUser.value?.id === data.user.id
    currentUser.value = data.user; authPassword.value = ''; authConfirmPassword.value = ''; showAuth.value = false
    if (!sameUser) await loadWorkspace()
    flash(authMode.value === 'register' ? '注册成功，已登录个人工作区' : '已登录个人工作区')
  } catch (ex) { authError.value = ex instanceof Error ? ex.message : '登录失败' }
  finally { authBusy.value = false }
}
function openAuth() { authMode.value = 'login'; authError.value = ''; authEmail.value = currentUser.value?.email ?? ''; authPassword.value = ''; authConfirmPassword.value = ''; showAuth.value = true }
async function logout() {
  try {
    if (sessionChanged.value) { if (!confirm('账户已变化，退出前请先导出草稿。继续会重新载入本机数据。')) return }
    else await preserveDraftsBeforeSwitch()
    const result = await fetch('/api/auth/logout', { method: 'POST' }); if (!result.ok) throw new Error('退出失败')
    currentUser.value = null; await loadWorkspace(); flash('已退出，切换到本机浏览器工作区')
  } catch (ex) { saveError.value = ex instanceof Error ? ex.message : '退出失败' }
}
async function importGuestData() {
  if (!currentUser.value || saving.value || guestImportBusy.value) return
  guestImportBusy.value = true
  try {
    if (conflict.value || sessionChanged.value) throw new Error('请先解决保存冲突或重新载入账户')
    const guest = await readGuestWorkspace()
    if (await importedGuestRevision(currentUser.value.id) === (guest.revision ?? 0)) { guestAvailable.value = false; flash('当前版本的本机数据已导入'); return }
    const imported = parseImport(guest)
    workspace.value.collections.push(...imported.collections); workspace.value.environments.push(...imported.environments)
    if (await persist()) { guestAvailable.value = false; try { await markGuestImported(currentUser.value.id, guest.revision ?? 0) } catch { /* The account copy is already saved; the banner may reappear. */ } flash('本机集合和环境已复制到账户，原本机数据保留') }
  } catch (ex) { saveError.value = ex instanceof Error ? ex.message : '导入失败' }
  finally { guestImportBusy.value = false }
}
async function loadShares(append = false) {
  const generation = ++sharesGeneration; sharesBusy.value = true; sharesError.value = ''
  try {
    const query = new URLSearchParams({ query: shareQuery.value, kind: shareKind.value, mine: String(shareMine.value && !!currentUser.value), offset: String(append ? shares.value.length : 0), limit: '30' })
    const result = await fetch(`/api/shares?${query}`); const page = await result.json(); if (!result.ok) throw new Error(page.error ?? '加载分享失败')
    if (generation !== sharesGeneration) return
    shares.value = append ? [...shares.value, ...page.items] : page.items; shareTotal.value = page.total
  } catch (ex) { sharesError.value = ex instanceof Error ? ex.message : '加载分享失败' }
  finally { if (generation === sharesGeneration) sharesBusy.value = false }
}
function openShares() { switchSidebar('shares'); if (sidebarTab.value === 'shares') void loadShares() }
async function viewShare(item: Pick<ShareSummary, 'id'>) {
  const generation = ++detailGeneration
  sharesError.value = ''
  try {
    const result = await fetch(`/api/shares/${item.id}`); const data = await result.json(); if (!result.ok) throw new Error(data.error ?? '分享已撤回')
    if (generation !== detailGeneration) return
    shareDetail.value = data; shareDestination.value = selectedCollectionId.value
  } catch (ex) { sharesError.value = ex instanceof Error ? ex.message : '加载失败' }
}
async function copyShareLink() {
  if (!shareDetail.value) return
  const url = `${window.location.origin}${window.location.pathname}?share=${encodeURIComponent(shareDetail.value.id)}`
  try { await navigator.clipboard.writeText(url); flash('分享链接已复制') }
  catch { sharesError.value = `请手动复制分享链接：${url}` }
}
async function copyShare() {
  if (!shareDetail.value || saving.value) return
  try {
    if (conflict.value || sessionChanged.value || (workspaceDirty.value && saveError.value)) throw new Error('请先处理工作区的保存问题，已复制的内容会保留')
    const imported = parseImport({ collections: [shareDetail.value.collection], environments: [] }).collections[0]
    imported.name = shareDetail.value.title
    if (shareDetail.value.kind === 'request' && imported.requests[0]) imported.requests[0].name = shareDetail.value.title
    const destination = shareDetail.value.kind === 'request' ? workspace.value.collections.find(x => x.id === shareDestination.value) : undefined
    if (destination) destination.requests.push(...imported.requests.map(x => ({ ...x, folderId: '' })))
    else workspace.value.collections.push(imported)
    if (await persist()) {
      const collection = destination ?? imported; const request = destination ? destination.requests[destination.requests.length - 1] : collection.requests[0]
      sidebarTab.value = 'collections'; if (request) openRequest(collection, request)
      flash(currentUser.value ? '已保存到我的账户，可独立修改' : '已保存到本机浏览器，可独立修改')
    }
  } catch (ex) { sharesError.value = ex instanceof Error ? ex.message : '保存失败' }
}
async function withdrawShare(item: ShareSummary) {
  if (!confirm('撤回后其他人不能再浏览此分享，已保存的副本不受影响。继续？')) return
  try {
  const result = await accountFetch(`/api/shares/${item.id}`, { method: 'DELETE' })
  if (!result.ok) { sharesError.value = '撤回失败，只有作者本人可以操作'; return }
  if (shareDetail.value?.id === item.id) shareDetail.value = null
  await loadShares(); flash('分享已撤回')
  } catch (ex) { sharesError.value = ex instanceof Error ? ex.message : '撤回失败' }
}
async function prepareShare(kind: 'request' | 'collection', collectionId = selectedCollectionId.value) {
  if (!currentUser.value) { openAuth(); authError.value = '登录后可发布分享；游客可浏览并保存别人的分享'; return }
  shareError.value = ''; shareBusy.value = true
  try {
    if (kind === 'request') { await saveRequest(); if (saveError.value) throw new Error(saveError.value); collectionId = selectedCollectionId.value }
    const collection = workspace.value.collections.find(x => x.id === collectionId); if (!collection) throw new Error('请先保存集合')
    shareDraft.value = { kind, collectionId, requestId: kind === 'request' ? draft.value.id : '', title: kind === 'request' ? draft.value.name : collection.name, description: '', revision: -1 }
    const result = await accountFetch('/api/shares/preview', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(shareDraft.value) })
    const data = await result.json(); if (!result.ok) throw new Error(data.error ?? '预览失败')
    sharePreview.value = data.collection; shareDraft.value.revision = data.revision; showShare.value = true
  } catch (ex) { saveError.value = ex instanceof Error ? ex.message : '分享失败' }
  finally { shareBusy.value = false }
}
async function publishShare() {
  if (shareBusy.value) return
  shareBusy.value = true; shareError.value = ''
  try {
    const result = await accountFetch('/api/shares', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(shareDraft.value) })
    const data = await result.json(); if (!result.ok) throw new Error(data.error ?? '分享失败')
    showShare.value = false; shareDetail.value = data; sidebarTab.value = 'shares'; await loadShares(); flash('已发布到分享大厅')
  } catch (ex) { shareError.value = ex instanceof Error ? ex.message : '分享失败' }
  finally { shareBusy.value = false }
}
function openRequest(collection: Collection, request: RequestItem) {
  const existing = editorTabs.value.find(x => x.requestId === request.id && x.originalCollectionId === collection.id)
  if (existing) activateEditor(existing); else addEditor(request, collection.id, true)
}

function createCollection() {
  const collection: Collection = { id: uid(), name: '新建集合', auth: newAuth(), folders: [], requests: [] }
  workspace.value.collections.push(collection)
  selectedCollectionId.value = collection.id
  createRequest(collection.id)
  persist()
}

function createFolder(collection: Collection) {
  folderCreationCollectionId.value = collection.id
  newFolderName.value = ''
}

async function commitFolder(collection: Collection) {
  const name = newFolderName.value.trim()
  if (!name) return
  collection.folders.push({ id: uid(), name, auth: newAuth('inherit') })
  folderCreationCollectionId.value = ''
  newFolderName.value = ''
  await persist()
}

function createRequest(collectionId?: string, folderId = '') {
  addEditor(newRequest(folderId), collectionId ?? selectedCollectionId.value)
}
async function saveRequest() {
  if (saving.value) return
  error.value = ''; stashEditor()
  const tab = editorTabs.value.find(x => x.id === activeEditorId.value)!
  let collection = selectedCollection.value
  if (!collection) {
    collection = { id: uid(), name: '我的集合', auth: newAuth(), folders: [], requests: [] }
    workspace.value.collections.push(collection); selectedCollectionId.value = collection.id
  }
  if (draft.value.folderId && !collection.folders.some(x => x.id === draft.value.folderId)) draft.value.folderId = ''
  const saved = copy(draft.value); delete saved.files
  if (originalCollectionId.value && originalCollectionId.value !== collection.id) {
    const previous = workspace.value.collections.find(x => x.id === originalCollectionId.value)
    if (previous) previous.requests = previous.requests.filter(x => x.id !== saved.id)
  }
  const index = collection.requests.findIndex(x => x.id === saved.id)
  if (index >= 0) collection.requests[index] = saved; else collection.requests.push(saved)
  const targetCollectionId = collection.id
  const savedSignature = JSON.stringify([saved, targetCollectionId])
  selectedRequestId.value = saved.id; stashEditor()
  if (await persist()) {
    tab.originalCollectionId = targetCollectionId; tab.collectionId = targetCollectionId; tab.requestId = saved.id; tab.baseline = savedSignature
    if (activeEditorId.value === tab.id) { originalCollectionId.value = targetCollectionId; baseline.value = savedSignature }
    flash(`请求已保存到 ${collection.name}`)
  }
}
function duplicateRequest() {
  const cloned = copy(draft.value); cloned.id = uid(); cloned.name += ' 副本'
  addEditor(cloned, selectedCollectionId.value)
  baseline.value = ''; editorTabs.value[editorTabs.value.length - 1].baseline = ''
}
function openHistory(item: History) {
  const request = item.request ? copy(item.request) : { ...newRequest(), method: item.method, url: item.url, name: '历史请求' }
  request.id = uid(); if (item.effectiveAuth) request.auth = copy(item.effectiveAuth)
  addEditor(request, workspace.value.collections.some(x => x.id === item.collectionId) ? item.collectionId! : (workspace.value.collections[0]?.id ?? ''), false, item.variables ? copy(item.variables) : null)
  if (!item.request) error.value = '这是旧版历史记录，只保存了请求地址。'
}

async function saveAuthSettings() {
  if (await persist()) flash('验证设置已保存')
}

function onCollectionChanged() {
  if (!selectedCollection.value?.folders.some(x => x.id === draft.value.folderId)) draft.value.folderId = ''
}

async function deleteRequest() {
  const collection = workspace.value.collections.find(x => x.id === originalCollectionId.value)
  const requestId = selectedRequestId.value
  if (!requestId || !collection || !confirm('删除这个请求？')) return
  collection.requests = collection.requests.filter(x => x.id !== requestId)
  if (await persist()) {
    for (const tab of editorTabs.value.filter(x => x.requestId === requestId && x.originalCollectionId === collection.id)) {
      tab.requestId = ''; tab.originalCollectionId = ''; tab.baseline = ''
      if (tab.id === activeEditorId.value) { selectedRequestId.value = ''; originalCollectionId.value = ''; baseline.value = '' }
    }
    flash('请求已删除，打开的草稿已保留')
  }
}

async function deleteCollection(collection: Collection) {
  if (!confirm(`删除集合「${collection.name}」及其中所有请求？`)) return
  workspace.value.collections = workspace.value.collections.filter(x => x.id !== collection.id)
  if (await persist()) {
    for (const tab of editorTabs.value.filter(x => x.collectionId === collection.id)) {
      tab.collectionId = ''; tab.originalCollectionId = ''; tab.requestId = ''; tab.draft.folderId = ''; tab.baseline = ''
      if (tab.id === activeEditorId.value) { selectedCollectionId.value = ''; selectedRequestId.value = ''; originalCollectionId.value = ''; draft.value.folderId = ''; baseline.value = '' }
    }
    flash('集合已删除，打开的草稿已保留')
  }
}

async function renameCollection(collection: Collection) {
  const name = prompt('集合名称', collection.name)?.trim()
  if (name) { collection.name = name; await persist() }
}

async function sendRequest() {
  if (busy.value || !draft.value.url.trim()) return
  error.value = ''; response.value = null; busy.value = true; requestController = new AbortController(); stashEditor()
  const tab = editorTabs.value.find(x => x.id === activeEditorId.value)!
  const controller = requestController
  const generation = storageGeneration; const owner = storageOwner()
  try {
    const spec = { ...copy(draft.value), files: draft.value.bodyType === 'multipart' ? files.value : [] }
    const result = await accountFetch('/api/execute', { method: 'POST', headers: { 'Content-Type': 'application/json' }, signal: controller.signal,
      body: JSON.stringify({ request: spec, variables: variableMap.value, effectiveAuth: effectiveAuth.value, collectionId: selectedCollectionId.value }) }, owner)
    const data = await result.json()
    if (!result.ok) throw new Error(data.error ?? '请求发送失败')
    if (generation !== storageGeneration) return
    if (!currentUser.value && data.history) await addGuestHistory(data.history)
    tab.response = data as ExecuteResult
    if (activeEditorId.value === tab.id) response.value = tab.response
    try { await loadHistory() } catch { flash('响应已收到，历史记录暂未刷新') }
  } catch (ex) {
    tab.error = ex instanceof DOMException && ex.name === 'AbortError' ? '请求已取消' : ex instanceof Error ? ex.message : '请求发送失败'
    if (generation === storageGeneration && activeEditorId.value === tab.id) error.value = tab.error
  } finally {
    tab.busy = false; tab.controller = null
    if (activeEditorId.value === tab.id) { busy.value = false; requestController = null }
  }
}
async function copyCurl() {
  try { await navigator.clipboard.writeText(curlCommand(draft.value, effectiveAuth.value, variableMap.value, files.value)); flash('已复制 cURL（包含当前授权信息）') }
  catch { error.value = '剪贴板不可用，请在 localhost 或 HTTPS 下使用' }
}
function downloadResponse() {
  if (!response.value) return
  const result = response.value
  const body = result.bodyBase64 ? Uint8Array.from(atob(result.bodyBase64), char => char.charCodeAt(0)) : result.body
  downloadBlob(new Blob([body], { type: result.contentType || 'application/octet-stream' }), result.fileName || 'response.txt')
}
function downloadBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob); const a = document.createElement('a'); a.href = url; a.download = name; a.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

async function addFiles(event: Event) {
  const input = event.target as HTMLInputElement
  const selected = Array.from(input.files ?? [])
  const uploadFiles = files.value; const uploadRequest = draft.value
  const uploadTabId = activeEditorId.value
  try {
  let total = uploadFiles.reduce((sum, file) => sum + file.base64.length * 0.75, 0)
  for (const file of selected) {
    if (uploadFiles.length >= 10 || total + file.size > 20 * 1024 * 1024) { throw new Error('最多上传 10 个文件，合计不能超过 20 MB') }
    if (file.size > 10 * 1024 * 1024) { throw new Error(`${file.name} 超过 10 MB`) }
    const base64 = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader()
      reader.onload = () => resolve(String(reader.result).split(',')[1] ?? '')
      reader.onerror = () => reject(new Error('文件读取失败'))
      reader.readAsDataURL(file)
    })
    total += file.size
    const field = uploadRequest.fileFields?.find(x => x.fileName === file.name) ?? uploadRequest.fileFields?.find(x => !uploadFiles.some(f => f.key === x.key))
    uploadFiles.push({ key: field?.key ?? 'file', fileName: file.name, contentType: file.type || 'application/octet-stream', base64 })
  }
  } catch (ex) { const message = ex instanceof Error ? ex.message : '文件读取失败'; const tab = editorTabs.value.find(x => x.id === uploadTabId); if (tab) tab.error = message; if (activeEditorId.value === uploadTabId) error.value = message }
  input.value = ''
}

async function saveEnvironments() { if (await persist()) showEnvironments.value = false }

function addEnvironment() {
  const environment: Environment = { id: uid(), name: '新建环境', variables: [newRow()] }
  workspace.value.environments.push(environment)
  activeEnvironmentId.value = environment.id
  persist()
}

async function exportWorkspace() {
  const generation = storageGeneration; const owner = storageOwner(); const account = currentUser.value
  stashEditor(); const snapshot = copy(workspace.value)
  const drafts = editorTabs.value.filter(tabDirty).map(x => ({ request: { ...copy(x.draft), auth: copy(resolveAuth(x.draft, workspace.value.collections.find(c => c.id === x.collectionId))) }, collectionId: x.collectionId }))
  try {
    const history: History[] = account ? [] : await readGuestHistory()
    for (let offset = 0; account; offset += 100) {
      const result = await accountFetch(`/api/history?offset=${offset}&limit=100`, {}, owner); if (!result.ok) { history.push(...snapshot.history); flash('登录已变化，已导出当前缓存与草稿'); break }
      const page = await result.json(); history.push(...page.items); if (offset + page.items.length >= page.total || !page.items.length) break
    }
    if (generation !== storageGeneration || owner !== storageOwner()) throw new Error('账户已切换，请在当前账户重新导出')
    downloadBlob(new Blob([JSON.stringify({ ...snapshot, history, schemaVersion: 2, unsavedTabs: drafts }, null, 2)], { type: 'application/json' }), 'webpostman-workspace.json')
  } catch (ex) { saveError.value = ex instanceof Error ? ex.message : '导出失败' }
}
async function importFile(event: Event) {
  const input = event.target as HTMLInputElement; const file = input.files?.[0]; if (!file) return
  const generation = storageGeneration
  try {
    if (saving.value) throw new Error('请等待当前保存完成后再导入')
    if (file.size > 20 * 1024 * 1024) throw new Error('导入文件不能超过 20 MB')
    const imported = parseImport(JSON.parse(await file.text()))
    if (generation !== storageGeneration) throw new Error('账户已切换，请在当前工作区重新导入')
    if (workspace.value.collections.length + imported.collections.length > 500 || workspace.value.environments.length + imported.environments.length > 100) throw new Error('导入后超过工作区数量限制')
    workspace.value.collections.push(...imported.collections); workspace.value.environments.push(...imported.environments)
    if (await persist()) { flash('导入完成'); if (imported.warnings.length) saveError.value = imported.warnings.join('；') }
    else { saveError.value += '。导入内容保留在页面中，可点击重试保存或导出备份。' }
  } catch (ex) { saveError.value = ex instanceof Error ? ex.message : '导入失败' }
  input.value = ''
}
function beforeUnload(event: BeforeUnloadEvent) {
  if (workspaceDirty.value || saving.value || editorTabs.value.some(tabDirty) || (sidebarTab.value === 'mocks' && mockWorkspace.value?.hasUnsaved())) { event.preventDefault(); event.returnValue = '' }
}
function keyboard(event: KeyboardEvent) {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
    event.preventDefault()
    if (showAuth.value || showShare.value || showMockDestination.value || (sidebarTab.value === 'shares' || sidebarTab.value === 'mocks')) return
    if (!showEnvironments.value) void saveRequest(); else void persist()
  }
}
onMounted(async () => {
  window.addEventListener('keydown', keyboard); window.addEventListener('beforeunload', beforeUnload); await reloadWorkspace()
  const shareId = new URLSearchParams(window.location.search).get('share')
  if (shareId) { sidebarTab.value = 'shares'; await loadShares(); await viewShare({ id: shareId }) }
})
onBeforeUnmount(() => { window.removeEventListener('keydown', keyboard); window.removeEventListener('beforeunload', beforeUnload); clearTimeout(noticeTimer); requestController?.abort(); for (const tab of editorTabs.value) tab.controller?.abort() })
</script>

<template>
  <div class="app-shell">
    <header class="topbar">
      <div class="brand"><button class="sidebar-toggle" :title="sidebarCollapsed ? '展开侧栏' : '收起侧栏'" @click="sidebarCollapsed = !sidebarCollapsed">☰</button><div class="brand-mark">◆</div><span>WebPostman</span><small>API WORKSPACE</small></div>
      <div class="topbar-right">
        <span class="local-badge"><span class="live-dot"></span>{{ currentUser ? '账户存储' : '游客 · 本机存储' }}</span><button class="account-button" @click="openAuth" :disabled="workspaceLoading" :title="currentUser ? '切换账户' : '登录后保存到个人账户'">{{ currentUser?.email ?? '登录 / 注册' }}</button><button v-if="currentUser" class="top-link" @click="logout">退出</button><button class="top-link" @click="reloadWorkspace">重新载入</button>
        <button class="top-link" @click="showEnvironments = true">环境变量</button>
        <button class="top-link" @click="exportWorkspace">导出</button>
        <label class="top-link upload-label">导入<input type="file" accept=".json,application/json" hidden @change="importFile" /></label>
      </div>
    </header>

    <div v-if="currentUser && guestAvailable && !workspaceLoading" class="storage-notice"><span>本机浏览器中还有游客数据，是否复制到当前账户？原本机数据将保留。</span><button @click="importGuestData" :disabled="saving || guestImportBusy">导入本机数据</button><button @click="guestAvailable = false" aria-label="暂不导入">稍后</button></div>
    <div v-if="!currentUser" class="guest-notice">游客请求仅保存在此浏览器，清除浏览器数据后会丢失。登录后可保存到个人账户，或随时导出备份。</div>
    <div v-if="saveError" class="workspace-alert" role="alert"><span>{{ saveError }}</span><button @click="persist" :disabled="saving || conflict">重试保存</button><button @click="exportWorkspace">导出备份</button><button v-if="conflict" @click="reloadWorkspace">载入最新工作区</button><button v-else @click="saveError = ''" aria-label="关闭提示">×</button></div>
    <div v-if="workspaceLoading" class="storage-notice" role="status">正在载入工作区…</div>
    <div class="workspace-layout" :inert="workspaceLoading" :class="{ 'sidebar-is-collapsed': sidebarCollapsed }">
      <aside class="sidebar">
        <div class="sidebar-head"><span class="eyebrow">WORKSPACE</span><button v-if="sidebarTab !== 'mocks'" class="icon-button" title="新建集合" aria-label="新建集合" @click="createCollection"><svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.5v11M2.5 8h11" /></svg></button></div>
        <div class="sidebar-tabs"><button :class="{ active: sidebarTab === 'collections' }" @click="switchSidebar('collections')">集合</button><button :class="{ active: sidebarTab === 'history' }" @click="switchSidebar('history')">历史</button><button :class="{ active: sidebarTab === 'shares' }" @click="openShares">分享</button><button :class="{ active: sidebarTab === 'mocks' }" @click="switchSidebar('mocks')">模拟</button></div>
        <div v-if="sidebarTab === 'collections' || sidebarTab === 'history'" class="sidebar-search"><input v-model="search" aria-label="搜索集合或请求" placeholder="搜索名称、URL、方法…" /></div>
        <div v-if="sidebarTab === 'collections'" class="sidebar-scroll">
          <div v-if="!workspace.collections.length" class="empty-sidebar">还没有集合。<button @click="createCollection">创建第一个集合 →</button></div>
          <div v-for="collection in workspace.collections.filter(matchesCollection)" :key="collection.id" class="collection-block">
            <div class="collection-line" :class="{ selected: selectedCollectionId === collection.id }">
              <button class="tiny-button" :aria-label="`折叠集合 ${collection.name}`" :aria-expanded="!collapsedCollections.includes(collection.id)" @click="toggle(collapsedCollections, collection.id)">{{ collapsedCollections.includes(collection.id) ? '▸' : '▾' }}</button><span class="collection-name">{{ collection.name }}</span>
              <button class="tiny-button" title="新建请求" @click="createRequest(collection.id)">＋</button>
              <button class="tiny-button more-button" title="重命名" @click="renameCollection(collection)">⋯</button>
            </div>
            <template v-if="search || !collapsedCollections.includes(collection.id)">
            <div v-for="{ folder, depth } in folderRows(collection)" :key="folder.id" class="folder-group" :style="{ marginLeft: `${depth * 10}px` }">
              <div class="folder-line"><button class="tiny-button" :aria-label="`折叠文件夹 ${folder.name}`" :aria-expanded="!collapsedFolders.includes(folder.id)" @click="toggle(collapsedFolders, folder.id)">{{ collapsedFolders.includes(folder.id) ? '▸' : '▾' }}</button><span :title="folderPath(collection, folder)">{{ folder.name }}</span><button class="tiny-button" title="在文件夹中新建请求" @click="createRequest(collection.id, folder.id)">＋</button></div>
              <template v-if="search || !collapsedFolders.includes(folder.id)"><button v-for="request in shownRequests(collection, folder.id)" :key="request.id" class="request-line nested" :class="{ active: selectedRequestId === request.id }" @click="openRequest(collection, request)"><span :class="['method-mini', methodClass(request.method)]">{{ request.method }}</span><span class="truncate">{{ request.name }}</span></button></template>
            </div>
            <button v-for="request in shownRequests(collection)" :key="request.id" class="request-line" :class="{ active: selectedRequestId === request.id }" @click="openRequest(collection, request)"><span :class="['method-mini', methodClass(request.method)]">{{ request.method }}</span><span class="truncate">{{ request.name }}</span></button>
            </template>
            <div v-if="selectedCollectionId === collection.id" class="collection-actions"><button @click="createFolder(collection)">＋ 文件夹</button><button @click="prepareShare('collection', collection.id)" :disabled="saving || shareBusy">分享集合</button><button @click="deleteCollection(collection)">删除集合</button></div>
            <div v-if="folderCreationCollectionId === collection.id" class="folder-create"><input v-model="newFolderName" aria-label="文件夹名称" placeholder="文件夹名称" @keydown.enter="commitFolder(collection)" /><button @click="commitFolder(collection)">保存</button><button @click="folderCreationCollectionId = ''">取消</button></div>
          </div>
        </div>
        <div v-else-if="sidebarTab === 'history'" class="sidebar-scroll history-list"><div v-if="!workspace.history.length" class="empty-sidebar">发送请求后会显示在这里。</div><button v-for="item in workspace.history.filter(x => !search || `${x.request?.name ?? ''} ${x.url} ${x.method}`.toLowerCase().includes(search.toLowerCase()))" :key="item.id" class="history-line" :title="`${new Date(item.at).toLocaleString()} · ${item.status}`" @click="openHistory(item)"><span :class="['method-mini', methodClass(item.method)]">{{ item.method }}</span><span class="truncate">{{ item.request?.name || item.url }}</span><small>{{ item.status }}</small></button><button v-if="workspace.history.length < historyTotal" class="add-row" @click="loadHistory(true)">加载更多历史</button></div>
        <div v-if="sidebarTab === 'shares'" class="share-sidebar">
          <form class="share-filters" @submit.prevent="loadShares()"><input v-model="shareQuery" aria-label="搜索分享" placeholder="搜索分享标题、描述…" maxlength="200" /><div><select v-model="shareKind" aria-label="分享类型" @change="loadShares()"><option value="">全部类型</option><option value="request">请求</option><option value="collection">集合</option></select><button type="submit">搜索</button></div><label v-if="currentUser"><input v-model="shareMine" type="checkbox" @change="loadShares()" />只看我的分享</label></form>
          <div v-if="sharesError" class="share-inline-error" role="alert">{{ sharesError }}</div>
          <p v-if="!shares.length && !sharesBusy" class="empty-sidebar">还没有匹配的分享。登录后可分享请求或集合。</p>
          <div class="share-list"><button v-for="item in shares" :key="item.id" class="share-list-item" :class="{ selected: shareDetail?.id === item.id }" @click="viewShare(item)"><span class="share-kind">{{ item.kind === 'request' ? '请求' : '集合' }} · {{ item.requestCount }} 个请求</span><strong>{{ item.title }}</strong><small>{{ item.author }}</small></button><button v-if="shares.length < shareTotal" class="add-row" @click="loadShares(true)" :disabled="sharesBusy">加载更多分享</button><p v-if="sharesBusy" class="muted">正在加载…</p></div>
        </div>
        <div v-if="sidebarTab === 'mocks'" id="mock-sidebar"></div>
        <div class="sidebar-footer"><span class="footer-dot"></span> {{ currentUser ? '个人账户保存' : '浏览器 IndexedDB' }} <span class="save-state">{{ saving ? '保存中…' : saveError ? '保存需处理' : workspaceDirty || dirty ? '有未保存修改' : '已保存' }}</span></div>
      </aside>

      <MockWorkspace v-if="sidebarTab === 'mocks'" :key="currentUser?.id ?? 'guest'" ref="mockWorkspace" :user="currentUser" :recording="mockRecording" :target-server-id="mockTargetServerId" :request="accountFetch" @login="openAuth" @consumed="mockRecording = null" @notice="flash" />
      <main v-else-if="sidebarTab === 'shares'" class="main-panel share-panel">
        <div class="share-panel-heading"><div><span class="eyebrow">COMMUNITY</span><h1>分享大厅</h1><p>登录或游客均可浏览，保存副本后可以独立修改。</p></div><button class="outline-button" @click="loadShares()">刷新分享</button></div>
        <div v-if="shareDetail" class="shared-detail">
          <div class="share-title-row"><div><span class="share-kind">{{ shareDetail.kind === 'request' ? '请求分享' : '集合分享' }}</span><h2>{{ shareDetail.title }}</h2><p>{{ shareDetail.author }} · {{ new Date(shareDetail.createdAt).toLocaleString() }}</p></div><div class="share-title-actions"><button class="outline-button" @click="copyShareLink">复制分享链接</button><button v-if="shareDetail.ownerId === currentUser?.id" class="subtle-button" @click="withdrawShare(shareDetail)">撤回分享</button></div></div>
          <p v-if="shareDetail.description" class="share-description">{{ shareDetail.description }}</p>
          <div class="share-copy-bar"><label v-if="shareDetail.kind === 'request'">保存到<select v-model="shareDestination" aria-label="分享保存目标"><option value="">新建集合</option><option v-for="collection in workspace.collections" :key="collection.id" :value="collection.id">{{ collection.name }}</option></select></label><span v-else>将创建独立的集合副本</span><button class="send-button" @click="copyShare" :disabled="saving">{{ saving ? '保存中…' : currentUser ? '存到我的集合' : '存到本机集合' }}</button></div>
          <h3>分享的请求（{{ shareDetail.collection.requests.length }}）</h3><div class="shared-request" v-for="request in shareDetail.collection.requests" :key="request.id"><span :class="['method-mini', methodClass(request.method)]">{{ request.method }}</span><strong>{{ request.name }}</strong><code>{{ request.url }}</code></div>
          <details class="share-source"><summary>查看完整内容（敏感凭据需自行配置）</summary><pre>{{ JSON.stringify(shareDetail.collection, null, 2) }}</pre></details>
        </div>
        <div v-else class="share-empty"><strong>发现可复用的请求与集合</strong><p>从左侧选择分享查看详情，一键存到自己的存储中。</p><button v-if="!currentUser" class="outline-button" @click="openAuth">登录，发布我的分享</button></div>
      </main>
      <main v-else class="main-panel">
        <div class="request-tabs" role="tablist" aria-label="请求标签"><div v-for="tab in editorTabs" :key="tab.id" class="request-tab" :class="{ active: activeEditorId === tab.id }"><button role="tab" :aria-selected="activeEditorId === tab.id" @click="activateEditor(tab)">{{ tab.busy ? '◌ ' : '' }}{{ tab.draft.name }}{{ tabDirty(tab) ? ' •' : '' }}</button><button :aria-label="`关闭标签 ${tab.draft.name}`" @click="closeEditor(tab)">×</button></div><button class="tiny-button" aria-label="新建请求标签" @click="createRequest()">＋</button></div>
        <div class="editor-header">
          <div class="editor-identity"><input v-model="draft.name" class="request-title" aria-label="请求名称" /><div class="category-picker"><span>保存到</span><select v-model="selectedCollectionId" aria-label="选择集合" @change="onCollectionChanged"><option value="">新建集合</option><option v-for="collection in workspace.collections" :key="collection.id" :value="collection.id">{{ collection.name }}</option></select><select v-if="selectedCollection" v-model="draft.folderId" aria-label="选择文件夹"><option value="">集合根目录</option><option v-for="folder in selectedCollection.folders" :key="folder.id" :value="folder.id">{{ folderPath(selectedCollection, folder) }}</option></select></div></div>
          <div class="header-actions"><span v-if="dirty" class="dirty-badge">未保存</span><button class="subtle-button" @click="duplicateRequest">复制请求</button><button class="subtle-button" @click="prepareShare('request')" :disabled="saving || shareBusy">分享请求</button><button class="subtle-button" @click="copyCurl" title="复制为 Bash cURL，包含当前授权信息">复制 cURL</button><button class="subtle-button" @click="deleteRequest" :disabled="!selectedRequestId">删除</button><button class="outline-button" @click="saveRequest" :disabled="saving" title="Ctrl+S / ⌘S">保存请求</button></div>
        </div>
        <section class="composer">
          <div class="section-label"><span class="section-index">01</span> REQUEST BUILDER <span class="section-line"></span><span class="muted">配置并发送 HTTP 请求</span></div>
          <div class="url-bar"><select v-model="draft.method" aria-label="HTTP 方法" :class="methodClass(draft.method)"><option>GET</option><option>POST</option><option>PUT</option><option>PATCH</option><option>DELETE</option><option>HEAD</option><option>OPTIONS</option></select><input v-model="draft.url" type="url" placeholder="https://api.example.com/v1/users" aria-label="请求 URL" @keydown.enter="sendRequest" /><button v-if="busy" class="send-button cancel" @click="cancelRequest">取消</button><button v-else class="send-button" :disabled="!draft.url.trim()" @click="sendRequest">发送请求 <span>↗</span></button></div>
          <label class="ssl-option" :class="{ unsafe: draft.allowInsecureSsl }"><input v-model="draft.allowInsecureSsl" type="checkbox" /><span class="ssl-checkbox" aria-hidden="true">✓</span><span><strong>允许不安全的 SSL 连接</strong><small>用于自签名或证书不受信任的 HTTPS 服务</small></span><span v-if="draft.allowInsecureSsl" class="unsafe-badge">已跳过证书验证</span></label>
          <div class="request-options"><label>超时（秒）<input v-model.number="draft.timeoutSeconds" type="number" min="1" max="600" aria-label="请求超时秒数" /></label><label><input v-model="draft.followRedirects" type="checkbox" />跟随重定向</label><label><input v-model="draft.showSensitiveTrace" type="checkbox" />显示敏感诊断（含凭据与正文）</label></div>
          <div class="editor-tabs"><button v-for="tab in (['params', 'auth', 'headers', 'cookies', 'body'] as const)" :key="tab" :class="{ active: activeTab === tab }" @click="activeTab = tab">{{ { params: 'Params', auth: 'Authorization', headers: 'Headers', cookies: 'Cookies', body: 'Body' }[tab] }}<span v-if="tab === 'auth' && effectiveAuth.type !== 'none'" class="tab-dot"></span></button></div>

          <div class="editor-content">
            <template v-if="activeTab === 'params' || activeTab === 'headers' || activeTab === 'cookies'">
              <div class="pane-intro"><h3>{{ { params: '查询参数', headers: '请求头', cookies: '请求 Cookie' }[activeTab] }}</h3><p>{{ { params: '参数会追加到请求 URL，支持环境变量。', headers: '自定义请求头会覆盖同名系统默认值。', cookies: 'Cookie 名称和值会组合到 Cookie 请求头。' }[activeTab] }}</p></div>
              <div class="kv-head"><span>启用</span><span>KEY</span><span>VALUE</span><span></span></div>
              <div v-for="row in (activeTab === 'params' ? draft.params : activeTab === 'headers' ? draft.headers : (draft.cookies ?? []))" :key="row.id" class="kv-row">
                <input v-model="row.enabled" type="checkbox" aria-label="启用" />
                <div v-if="activeTab === 'headers'" class="header-key-combobox" @focusout="closeHeaderPicker($event, row.id)">
                  <input :id="`header-key-${row.id}`" v-model="row.key" type="text" role="combobox" aria-label="请求头名称" aria-autocomplete="list" :aria-expanded="openHeaderPickerId === row.id" :aria-controls="`header-options-${row.id}`" autocomplete="off" placeholder="选择或输入 Header" @focus="openHeaderPickerId = row.id" @keydown.down.prevent="focusHeaderOption(row.id, 0)" @keydown.esc="openHeaderPickerId = ''" />
                  <button type="button" class="header-picker-trigger" aria-label="选择常用请求头" @click="openHeaderPickerId = openHeaderPickerId === row.id ? '' : row.id"><svg viewBox="0 0 16 16" aria-hidden="true"><path d="m4 6 4 4 4-4" /></svg></button>
                  <div v-if="openHeaderPickerId === row.id" :id="`header-options-${row.id}`" class="header-options" role="listbox" aria-label="常用请求头">
                    <button v-for="(name, index) in matchingHeaderNames(row.key)" :key="name" type="button" role="option" :aria-selected="row.key === name" @click="chooseHeaderName(row, name)" @keydown.down.prevent="focusHeaderOption(row.id, index + 1)" @keydown.up.prevent="focusHeaderOption(row.id, index - 1)" @keydown.esc="openHeaderPickerId = ''">{{ name }}</button>
                    <div v-if="!matchingHeaderNames(row.key).length" class="header-options-empty">继续输入自定义请求头名称</div>
                  </div>
                </div>
                <input v-else v-model="row.key" placeholder="Key" />
                <input v-model="row.value" placeholder="Value" />
                <button class="remove-row" title="删除" @click="activeTab === 'params' ? draft.params = draft.params.filter(x => x.id !== row.id) : activeTab === 'headers' ? draft.headers = draft.headers.filter(x => x.id !== row.id) : draft.cookies = draft.cookies?.filter(x => x.id !== row.id)">×</button>
              </div>
              <button class="add-row" @click="activeTab === 'params' ? draft.params.push(newRow()) : activeTab === 'headers' ? draft.headers.push(newRow()) : draft.cookies?.push(newRow())">＋ 添加一行</button>
              <div v-if="activeTab === 'headers'" class="default-headers"><strong>系统默认请求头</strong><span>发送时自动添加；上方同名请求头可以覆盖</span><div v-for="header in defaultHeaders" :key="header.key"><code>{{ header.key }}</code><code>{{ header.value }}</code></div><small>Host、Content-Length 和 Content-Type 由 HTTP 客户端或正文类型决定。</small></div>
            </template>

            <template v-else-if="activeTab === 'auth'"><div class="pane-intro"><h3>身份验证</h3><p>可为集合、文件夹或当前请求设置验证信息。请求设置优先。</p></div><div class="auth-scope"><span>配置范围</span><button :class="{ chosen: authScope === 'request' }" @click="authScope = 'request'">当前请求</button><button :disabled="!selectedFolder" :class="{ chosen: authScope === 'folder' }" @click="authScope = 'folder'">所在文件夹</button><button :disabled="!selectedCollection" :class="{ chosen: authScope === 'collection' }" @click="authScope = 'collection'">所属集合</button></div><div class="auth-grid"><div class="form-field"><label>验证类型</label><select v-model="activeAuth.type"><option v-if="authScope !== 'collection'" value="inherit">继承上级设置</option><option value="none">No Auth</option><option value="basic">Basic Auth</option><option value="bearer">Bearer Token</option><option value="apiKey">API Key</option></select></div><template v-if="activeAuth.type === 'basic'"><div class="form-field"><label>用户名</label><input v-model="activeAuth.username" autocomplete="off" placeholder="username 或 {{username}}" /></div><div class="form-field"><label>密码</label><input v-model="activeAuth.password" type="password" autocomplete="off" placeholder="password 或 {{password}}" /></div></template><template v-if="activeAuth.type === 'bearer'"><div class="form-field wide"><label>Token</label><input v-model="activeAuth.token" type="password" autocomplete="off" placeholder="粘贴 Token 或使用 {{token}}" /></div></template><template v-if="activeAuth.type === 'apiKey'"><div class="form-field"><label>Key</label><input v-model="activeAuth.key" placeholder="X-API-Key" /></div><div class="form-field"><label>Value</label><input v-model="activeAuth.value" type="password" autocomplete="off" placeholder="API Key 或 {{api_key}}" /></div><div class="form-field"><label>添加到</label><select v-model="activeAuth.in"><option value="header">请求头 Header</option><option value="query">查询参数 Query</option></select></div></template></div><div class="auth-note">当前生效：<strong>{{ { inherit: '继承', none: 'No Auth', basic: 'Basic Auth', bearer: 'Bearer Token', apiKey: 'API Key' }[effectiveAuth.type] }}</strong><span> · 修改集合或文件夹验证后请点击保存</span></div><button v-if="authScope !== 'request'" class="outline-button auth-save" @click="saveAuthSettings">保存验证设置</button></template>

            <template v-else><div class="pane-intro"><h3>请求正文</h3><p>选择正文格式。GET 和 HEAD 请求不会发送正文。</p></div><div class="body-options"><label v-for="option in [{ value: 'none', label: 'None' }, { value: 'json', label: 'JSON' }, { value: 'text', label: 'Text' }, { value: 'form', label: 'Form URL Encoded' }, { value: 'multipart', label: 'Multipart' }]" :key="option.value"><input v-model="draft.bodyType" type="radio" :value="option.value" />{{ option.label }}</label></div><textarea v-if="draft.bodyType === 'json' || draft.bodyType === 'text'" v-model="draft.body" class="body-editor" spellcheck="false" :placeholder="draft.bodyType === 'json' ? '{\n  &quot;name&quot;: &quot;example&quot;\n}' : '在这里输入请求正文…'"></textarea><template v-else-if="draft.bodyType === 'form' || draft.bodyType === 'multipart'"><div class="kv-head"><span>启用</span><span>KEY</span><span>VALUE</span><span></span></div><div v-for="row in draft.form" :key="row.id" class="kv-row"><input v-model="row.enabled" type="checkbox" /><input v-model="row.key" placeholder="Key" /><input v-model="row.value" placeholder="Value" /><button class="remove-row" @click="draft.form = draft.form.filter(x => x.id !== row.id)">×</button></div><button class="add-row" @click="draft.form.push(newRow())">＋ 添加字段</button><div v-if="draft.bodyType === 'multipart'" class="file-box"><label class="outline-button upload-label">＋ 添加文件<input type="file" multiple hidden @change="addFiles" /></label><span class="muted">每个文件最大 10 MB，合计 20 MB；文件只用于本次发送</span><div v-for="field in draft.fileFields ?? []" :key="`${field.key}:${field.fileName}`" class="file-row muted">导入的文件字段：{{ field.key }} · {{ field.fileName || '请选择文件' }}（需要重新选择本机文件）</div><div v-for="(file, index) in files" :key="index" class="file-row"><input v-model="file.key" aria-label="文件字段名" /><span>{{ file.fileName }}</span><button @click="files.splice(index, 1)">×</button></div></div></template></template>
          </div>
        </section>

        <section class="response-section">
          <div class="section-label"><span class="section-index">02</span> RESPONSE <span class="section-line"></span><span v-if="response" class="response-metrics"><span :class="response.status < 400 ? 'status-good' : 'status-bad'">{{ response.status }} {{ response.statusText }}</span><span>{{ response.durationMs }} ms</span><span>{{ (response.size / 1024).toFixed(1) }} KB</span></span></div>
          <div v-if="error" class="error-banner">{{ error }}</div>
          <div v-if="response" class="response-card">
            <div class="response-tabs"><button :class="{ active: responseTab === 'body' }" @click="responseTab = 'body'">Body</button><button :class="{ active: responseTab === 'headers' }" @click="responseTab = 'headers'">Headers <span>{{ Object.keys(response.headers).length }}</span></button><button :class="{ active: responseTab === 'cookies' }" @click="responseTab = 'cookies'">Cookies <span>{{ response.cookies.length }}</span></button><button :class="{ active: responseTab === 'trace' }" @click="responseTab = 'trace'">请求诊断</button><span class="content-type">{{ response.contentType || 'text/plain' }}</span></div>
            <div class="response-tools"><input v-model="responseSearch" aria-label="搜索响应正文" placeholder="搜索响应正文…" /><span v-if="responseSearch">{{ responseMatchCount >= 1000 ? '1000+' : responseMatchCount }} 处匹配</span><button class="outline-button" @click="downloadResponse">{{ response.truncated ? '下载已截取内容' : '下载响应' }}</button><button class="outline-button mock-record-button" @click="prepareMock" :disabled="response.truncated" :title="response.truncated ? '响应已截断，无法录制完整回放' : '将此次真实响应保存为可编辑的模拟接口'">创建模拟接口</button></div>
            <pre v-if="responseTab === 'body'" class="response-body"><template v-for="(piece, index) in responsePieces" :key="index"><mark v-if="piece.match">{{ piece.text }}</mark><template v-else>{{ piece.text }}</template></template></pre>
            <div v-else-if="responseTab === 'headers'" class="response-headers"><div v-for="(value, key) in response.headers" :key="key"><strong>{{ key }}</strong><span>{{ value }}</span></div></div>
            <div v-else-if="responseTab === 'cookies'" class="response-cookies"><p v-if="!response.cookies.length" class="muted">响应中没有 Set-Cookie。请求 Cookie 可在上方 Cookies 标签设置。</p><div v-for="(cookie, index) in response.cookies" :key="index" class="cookie-card"><strong>{{ cookie.name }}</strong><span>{{ cookie.value }}</span><small>{{ cookie.attributes || '无附加属性' }}</small></div></div>
            <div v-else class="trace-panel"><p v-if="response.trace.originalUrl && response.trace.originalUrl !== response.trace.url" class="trace-hint">原始地址：{{ response.trace.originalUrl }}</p><div class="trace-summary"><strong>{{ response.trace.method }}</strong><code>{{ response.trace.url }}</code><span>{{ response.trace.httpVersion }}</span></div><h4>阶段耗时</h4><div v-for="stage in response.trace.stages" :key="stage.name" class="trace-stage"><span>{{ stage.name }}</span><div class="trace-track"><i :style="{ width: `${Math.max(3, Math.round(stage.durationMs / Math.max(response.durationMs, 1) * 100))}%` }"></i></div><b>{{ stage.durationMs }} ms</b><small>{{ stage.detail }}</small></div><p class="trace-hint">等待响应头包含 DNS、TCP、TLS 和服务端处理；连接复用或重定向时无法分别精确计时。</p><h4>实际请求头 <small>默认隐藏敏感字段；以当前请求的诊断设置为准</small></h4><div class="trace-headers"><div v-for="(value, key) in response.trace.requestHeaders" :key="key"><code>{{ key }}</code><code>{{ value }}</code></div></div><h4 v-if="response.trace.requestBody">请求正文预览</h4><pre v-if="response.trace.requestBody" class="trace-body">{{ response.trace.requestBody }}</pre></div>
            <div v-if="response.truncated" class="truncated-note">响应超过 2 MB，只显示前 2 MB。</div>
          </div>
          <div v-else-if="!error" class="response-empty"><div class="response-empty-icon">↗</div><strong>响应将显示在这里</strong><span>输入 URL 并发送请求，查看状态、耗时、响应头与正文。</span></div>
        </section>
      </main>
    </div>

    <div v-if="showMockDestination" class="modal-backdrop account-backdrop" @click.self="cancelMockDestination" @keydown.esc.stop.prevent="cancelMockDestination">
      <form class="mock-destination-modal" role="dialog" aria-modal="true" aria-label="选择模拟接口保存位置" @submit.prevent="confirmMockDestination">
        <div class="modal-head"><h2>将响应添加到哪里？</h2><button type="button" class="close-button" aria-label="关闭模拟服务选择" @click="cancelMockDestination">×</button></div>
        <p>选择新建服务，或将本次响应添加到已有服务。确认后可检查、编辑并保存接口。</p>
        <div v-if="pendingMockRecording" class="mock-destination-source"><span class="method-mini">{{ pendingMockRecording.method }}</span><code>{{ pendingMockRecording.path }}</code><small>响应 {{ pendingMockRecording.response.status }}</small></div>
        <fieldset class="mock-destination-options"><legend>保存方式</legend>
          <label class="mock-destination-option" :class="{ chosen: mockDestinationMode === 'new' }"><input v-model="mockDestinationMode" type="radio" value="new" name="mock-destination" aria-label="新建模拟服务" /><span><strong>新建模拟服务</strong><small>设置一个新的名称和监听端口，再保存接口。</small></span></label>
          <label class="mock-destination-option" :class="{ chosen: mockDestinationMode === 'existing' }"><input v-model="mockDestinationMode" type="radio" value="existing" name="mock-destination" aria-label="添加到已有服务" :disabled="mockDestinationLoading || !mockDestinationServers.length || !!mockDestinationError" /><span><strong>添加到已有服务</strong><small>复用已有服务的端口，保留其中的其他接口。</small></span></label>
        </fieldset>
        <p v-if="mockDestinationLoading" class="muted" role="status">正在加载已有服务…</p>
        <p v-else-if="!mockDestinationServers.length && !mockDestinationError" class="mock-destination-hint">当前还没有模拟服务，请选择“新建模拟服务”。</p>
        <label v-if="mockDestinationMode === 'existing'" class="mock-destination-select">目标模拟服务<select v-model="mockDestinationId" aria-label="选择已有模拟服务" :disabled="mockDestinationLoading"><option value="" disabled>请选择一个已有服务</option><option v-for="server in mockDestinationServers" :key="server.id" :value="server.id">{{ server.name }} · :{{ server.port }} · {{ server.running ? '运行中' : '已停止' }} · {{ server.ruleCount }} 个接口</option></select></label>
        <div v-if="mockDestinationError" class="error-banner" role="alert">{{ mockDestinationError }} <button type="button" class="subtle-button" @click="loadMockDestinations" :disabled="mockDestinationLoading">重新加载服务</button></div>
        <div class="modal-actions"><button type="button" class="outline-button" @click="cancelMockDestination">取消</button><button type="submit" class="send-button" :disabled="!mockDestinationMode || (mockDestinationMode === 'existing' && (mockDestinationLoading || !mockDestinationId || !!mockDestinationError))">{{ mockDestinationMode === 'new' ? '下一步：设置新服务' : mockDestinationMode === 'existing' ? '添加到所选服务' : '请选择保存方式' }}</button></div>
      </form>
    </div>
    <div v-if="showAuth" class="modal-backdrop account-backdrop" @click.self="!authBusy && (showAuth = false)"><form class="account-modal" @submit.prevent="submitAuth" role="dialog" aria-modal="true" :aria-label="authMode === 'login' ? '登录账户' : '注册账户'"><div class="modal-head"><h2>{{ authMode === 'login' ? '登录个人工作区' : '创建 WebPostman 账户' }}</h2><button type="button" class="close-button" @click="showAuth = false" :disabled="authBusy" aria-label="关闭登录窗口">×</button></div><p>游客数据只在本机。登录后可以保存到个人账户、发布分享。</p><div class="account-mode"><button type="button" :class="{ chosen: authMode === 'login' }" @click="authMode = 'login'; authError = ''">登录</button><button type="button" :class="{ chosen: authMode === 'register' }" @click="authMode = 'register'; authError = ''">注册</button></div><label>邮箱<input v-model="authEmail" type="email" autocomplete="username" required maxlength="254" aria-label="账户邮箱" /></label><label>密码<input v-model="authPassword" type="password" :autocomplete="authMode === 'login' ? 'current-password' : 'new-password'" required minlength="8" maxlength="128" aria-label="账户密码" placeholder="至少 8 个字符" /></label><label v-if="authMode === 'register'">确认密码<input v-model="authConfirmPassword" type="password" autocomplete="new-password" required minlength="8" maxlength="128" aria-label="确认密码" /></label><p v-if="authError" class="error-banner" role="alert">{{ authError }}</p><button class="send-button account-submit" type="submit" :disabled="authBusy">{{ authBusy ? '正在处理…' : authMode === 'login' ? '登录' : '注册并登录' }}</button><small>切换前会将未保存草稿留在原存储中，本机数据不会自动上传。</small></form></div>
    <div v-if="showShare" class="modal-backdrop account-backdrop" @click.self="!shareBusy && (showShare = false)"><div class="share-modal" role="dialog" aria-modal="true" aria-label="发布分享"><div class="modal-head"><h2>发布{{ shareDraft.kind === 'request' ? '请求' : '集合' }}分享</h2><button class="close-button" @click="showShare = false" :disabled="shareBusy" aria-label="关闭分享预览">×</button></div><p>发布后，同一服务上的所有人都能查看并复制。常见认证信息、Cookie、文件内容已移除，非 JSON 原始正文默认不公开；请继续检查 URL 和正文是否含有私密内容。</p><label>分享标题<input v-model="shareDraft.title" maxlength="100" aria-label="分享标题" /></label><label>描述（选填）<textarea v-model="shareDraft.description" maxlength="1000" aria-label="分享描述" /></label><details open><summary>将公开的内容预览</summary><pre>{{ JSON.stringify(sharePreview, null, 2) }}</pre></details><p v-if="shareError" class="error-banner" role="alert">{{ shareError }}</p><div class="modal-actions"><button class="outline-button" @click="showShare = false" :disabled="shareBusy">取消</button><button class="send-button" @click="publishShare" :disabled="shareBusy || !shareDraft.title.trim()">{{ shareBusy ? '发布中…' : '确认公开分享' }}</button></div></div></div>
    <div v-if="showEnvironments" class="modal-backdrop" @click.self="showEnvironments = false"><div class="env-modal"><div class="modal-head"><div><span class="eyebrow">WORKSPACE SETTINGS</span><h2>环境变量</h2></div><button class="close-button" @click="showEnvironments = false">×</button></div><p class="modal-description">在 URL、请求头、正文及验证信息中使用 <code v-pre>{{变量名}}</code>。</p><div class="env-toolbar"><select v-model="activeEnvironmentId"><option value="">不使用环境</option><option v-for="environment in workspace.environments" :key="environment.id" :value="environment.id">{{ environment.name }}</option></select><button class="outline-button" @click="addEnvironment">＋ 新建环境</button></div><template v-if="activeEnvironment"><div class="form-field env-name"><label>环境名称</label><input v-model="activeEnvironment.name" /></div><div class="kv-head"><span>启用</span><span>KEY</span><span>VALUE</span><span></span></div><div v-for="row in activeEnvironment.variables" :key="row.id" class="kv-row"><input v-model="row.enabled" type="checkbox" /><input v-model="row.key" placeholder="base_url" /><input v-model="row.value" type="password" placeholder="变量值" /><button class="remove-row" @click="activeEnvironment!.variables = activeEnvironment!.variables.filter(x => x.id !== row.id)">×</button></div><button class="add-row" @click="activeEnvironment!.variables.push(newRow())">＋ 添加变量</button></template><div class="modal-actions"><button v-if="activeEnvironment" class="subtle-button" @click="workspace.environments = workspace.environments.filter(x => x.id !== activeEnvironmentId); activeEnvironmentId = ''; persist()">删除环境</button><button class="send-button" @click="saveEnvironments" :disabled="saving">保存并关闭</button></div></div></div>
    <div v-if="notice" class="toast">✓ {{ notice }}</div>
  </div>
</template>
