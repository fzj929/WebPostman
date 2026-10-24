<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { Auth, Collection, Environment, ExecuteResult, Folder, History, RequestItem, Row, Workspace, UploadFile } from './types'
import { newAuth, newRequest, newRow, uid } from './types'

const workspace = ref<Workspace>({ collections: [], environments: [], history: [] })
const selectedCollectionId = ref('')
const selectedRequestId = ref('')
const originalCollectionId = ref('')
const draft = ref<RequestItem>(newRequest())
const activeTab = ref<'params' | 'auth' | 'headers' | 'cookies' | 'body'>('params')
const openHeaderPickerId = ref('')
const responseTab = ref<'body' | 'headers' | 'cookies' | 'trace'>('body')
const sidebarTab = ref<'collections' | 'history'>('collections')
const sidebarCollapsed = ref(false)
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
const effectiveAuth = computed<Auth>(() => {
  if (draft.value.auth.type !== 'inherit') return draft.value.auth
  if (selectedFolder.value?.auth.type && selectedFolder.value.auth.type !== 'inherit') return selectedFolder.value.auth
  return selectedCollection.value?.auth ?? newAuth()
})
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

function persist(): Promise<boolean> {
  const snapshot = JSON.stringify(workspace.value)
  pendingSaves++
  saving.value = true
  saveQueue = saveQueue.then(async () => {
    try {
      const result = await fetch('/api/workspace', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: snapshot })
      if (!result.ok) throw new Error(`保存工作区失败 (${result.status})`)
      return true
    } catch (ex) { error.value = ex instanceof Error ? ex.message : '保存失败'; return false }
    finally { pendingSaves--; saving.value = pendingSaves > 0 }
  })
  return saveQueue
}

function openRequest(collection: Collection, request: RequestItem) {
  selectedCollectionId.value = collection.id
  originalCollectionId.value = collection.id
  selectedRequestId.value = request.id
  draft.value = copy(request)
  draft.value.cookies ??= [newRow()]
  draft.value.allowInsecureSsl ??= false
  files.value = []
  replayVariables.value = null
  authScope.value = 'request'
  response.value = null
  error.value = ''
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
  let collection = workspace.value.collections.find(x => x.id === collectionId)
  if (!collection) {
    collection = { id: uid(), name: '我的集合', auth: newAuth(), folders: [], requests: [] }
    workspace.value.collections.push(collection)
  }
  selectedCollectionId.value = collection.id
  originalCollectionId.value = ''
  selectedRequestId.value = ''
  draft.value = newRequest(folderId)
  replayVariables.value = null
  files.value = []
  response.value = null
  error.value = ''
}

async function saveRequest() {
  error.value = ''
  let collection = selectedCollection.value
  if (!collection) {
    collection = { id: uid(), name: '我的集合', auth: newAuth(), folders: [], requests: [] }
    workspace.value.collections.push(collection)
    selectedCollectionId.value = collection.id
  }
  if (draft.value.folderId && !collection.folders.some(x => x.id === draft.value.folderId)) draft.value.folderId = ''
  const saved = copy(draft.value)
  delete saved.files
  if (originalCollectionId.value && originalCollectionId.value !== collection.id) {
    const previous = workspace.value.collections.find(x => x.id === originalCollectionId.value)
    if (previous) previous.requests = previous.requests.filter(x => x.id !== saved.id)
  }
  const index = collection.requests.findIndex(x => x.id === saved.id)
  if (index >= 0) collection.requests[index] = saved
  else collection.requests.push(saved)
  selectedRequestId.value = saved.id
  if (await persist()) {
    originalCollectionId.value = collection.id
    flash(`请求已保存到 ${collection.name}${selectedFolder.value ? ' / ' + selectedFolder.value.name : ''}`)
  }
}

function openHistory(item: History) {
  selectedRequestId.value = ''
  originalCollectionId.value = ''
  selectedCollectionId.value = workspace.value.collections.some(x => x.id === item.collectionId) ? item.collectionId! : (workspace.value.collections[0]?.id ?? '')
  draft.value = item.request ? copy(item.request) : { ...newRequest(), method: item.method, url: item.url, name: '历史请求' }
  draft.value.id = uid()
  draft.value.cookies ??= [newRow()]
  draft.value.allowInsecureSsl ??= false
  if (item.effectiveAuth) draft.value.auth = copy(item.effectiveAuth)
  replayVariables.value = item.variables ? copy(item.variables) : null
  files.value = []
  response.value = null
  error.value = item.request ? '' : '这是旧版历史记录，只保存了请求地址。'
}

async function saveAuthSettings() {
  if (await persist()) flash('验证设置已保存')
}

function onCollectionChanged() {
  if (!selectedCollection.value?.folders.some(x => x.id === draft.value.folderId)) draft.value.folderId = ''
}

async function deleteRequest() {
  if (!selectedRequestId.value || !selectedCollection.value || !confirm('删除这个请求？')) return
  selectedCollection.value.requests = selectedCollection.value.requests.filter(x => x.id !== selectedRequestId.value)
  selectedRequestId.value = ''
  draft.value = newRequest()
  response.value = null
  await persist()
}

async function deleteCollection(collection: Collection) {
  if (!confirm(`删除集合「${collection.name}」及其中所有请求？`)) return
  workspace.value.collections = workspace.value.collections.filter(x => x.id !== collection.id)
  if (selectedCollectionId.value === collection.id) {
    selectedCollectionId.value = ''
    selectedRequestId.value = ''
    draft.value = newRequest()
  }
  await persist()
}

async function renameCollection(collection: Collection) {
  const name = prompt('集合名称', collection.name)?.trim()
  if (name) { collection.name = name; await persist() }
}

async function sendRequest() {
  error.value = ''
  response.value = null
  busy.value = true
  requestController = new AbortController()
  try {
    const spec = { ...draft.value, files: draft.value.bodyType === 'multipart' ? files.value : [] }
    const result = await fetch('/api/execute', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, signal: requestController.signal,
      body: JSON.stringify({ request: spec, variables: variableMap.value, effectiveAuth: effectiveAuth.value, collectionId: selectedCollectionId.value }),
    })
    const data = await result.json()
    if (!result.ok) throw new Error(data.error ?? '请求发送失败')
    response.value = data as ExecuteResult
    const current = await fetch('/api/workspace')
    if (current.ok) workspace.value.history = ((await current.json()) as Workspace).history
  } catch (ex) {
    error.value = ex instanceof DOMException && ex.name === 'AbortError' ? '请求已取消' : ex instanceof Error ? ex.message : '请求发送失败'
  } finally { busy.value = false; requestController = null }
}

async function addFiles(event: Event) {
  const input = event.target as HTMLInputElement
  const selected = Array.from(input.files ?? [])
  for (const file of selected) {
    if (file.size > 10 * 1024 * 1024) { error.value = `${file.name} 超过 10 MB`; continue }
    const base64 = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader()
      reader.onload = () => resolve(String(reader.result).split(',')[1] ?? '')
      reader.onerror = () => reject(new Error('文件读取失败'))
      reader.readAsDataURL(file)
    })
    files.value.push({ key: 'file', fileName: file.name, contentType: file.type || 'application/octet-stream', base64 })
  }
  input.value = ''
}

function addEnvironment() {
  const environment: Environment = { id: uid(), name: '新建环境', variables: [newRow()] }
  workspace.value.environments.push(environment)
  activeEnvironmentId.value = environment.id
  persist()
}

function exportWorkspace() {
  const blob = new Blob([JSON.stringify(workspace.value, null, 2)], { type: 'application/json' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url; a.download = 'webpostman-workspace.json'; a.click()
  URL.revokeObjectURL(url)
}

function postmanAuth(source: any): Auth {
  if (!source?.type) return newAuth('inherit')
  const get = (kind: string, key: string) => source[kind]?.find((x: any) => x.key === key)?.value ?? ''
  if (source.type === 'basic') return { ...newAuth('basic'), username: get('basic', 'username'), password: get('basic', 'password') }
  if (source.type === 'bearer') return { ...newAuth('bearer'), token: get('bearer', 'token') }
  if (source.type === 'apikey') return { ...newAuth('apiKey'), key: get('apikey', 'key'), value: get('apikey', 'value'), in: get('apikey', 'in') === 'query' ? 'query' : 'header' }
  return newAuth('none')
}

function postmanRows(rows: any[] = []): Row[] {
  return rows.map(x => ({ id: uid(), key: x.key ?? '', value: x.value ?? '', enabled: !x.disabled }))
}

function importPostman(data: any): Collection {
  const collection: Collection = { id: uid(), name: data.info?.name ?? '导入的集合', auth: postmanAuth(data.auth), folders: [], requests: [] }
  const visit = (items: any[], folderId = '') => {
    for (const item of items) {
      if (Array.isArray(item.item)) {
        const folder: Folder = { id: uid(), name: item.name ?? '文件夹', auth: postmanAuth(item.auth) }
        collection.folders.push(folder)
        visit(item.item, folder.id)
      } else if (item.request) {
        const source = item.request
        const request = newRequest(folderId)
        request.name = item.name ?? request.name
        request.method = source.method ?? 'GET'
        request.url = typeof source.url === 'string' ? source.url : source.url?.raw ?? ''
        if (source.url?.query?.length) request.url = request.url.split('?')[0]
        request.headers = postmanRows(source.header)
        request.params = postmanRows(source.url?.query)
        request.auth = postmanAuth(source.auth)
        const mode = source.body?.mode
        request.bodyType = mode === 'raw' ? (source.body?.options?.raw?.language === 'json' ? 'json' : 'text') : mode === 'urlencoded' ? 'form' : 'none'
        request.body = source.body?.raw ?? ''
        request.form = postmanRows(source.body?.urlencoded)
        collection.requests.push(request)
      }
    }
  }
  visit(data.item ?? [])
  return collection
}

async function importFile(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return
  try {
    const data = JSON.parse(await file.text())
    if (data.info && Array.isArray(data.item)) workspace.value.collections.push(importPostman(data))
    else if (Array.isArray(data.collections) && Array.isArray(data.environments)) {
      workspace.value.collections.push(...data.collections)
      workspace.value.environments.push(...data.environments)
    } else throw new Error('文件格式不支持')
    await persist()
    flash('导入完成')
  } catch (ex) { error.value = ex instanceof Error ? ex.message : '导入失败' }
  input.value = ''
}

onMounted(async () => {
  try {
    const result = await fetch('/api/workspace')
    if (!result.ok) throw new Error('无法连接后端服务')
    workspace.value = await result.json() as Workspace
    if (workspace.value.collections[0]) {
      const collection = workspace.value.collections[0]
      selectedCollectionId.value = collection.id
      if (collection.requests[0]) openRequest(collection, collection.requests[0])
    }
  } catch (ex) { error.value = ex instanceof Error ? ex.message : '加载失败' }
})
</script>

<template>
  <div class="app-shell">
    <header class="topbar">
      <div class="brand"><button class="sidebar-toggle" :title="sidebarCollapsed ? '展开侧栏' : '收起侧栏'" @click="sidebarCollapsed = !sidebarCollapsed">☰</button><div class="brand-mark">◆</div><span>WebPostman</span><small>API WORKSPACE</small></div>
      <div class="topbar-right">
        <span class="local-badge"><span class="live-dot"></span> 本地工作区</span>
        <button class="top-link" @click="showEnvironments = true">环境变量</button>
        <button class="top-link" @click="exportWorkspace">导出</button>
        <label class="top-link upload-label">导入<input type="file" accept=".json,application/json" hidden @change="importFile" /></label>
      </div>
    </header>

    <div class="workspace-layout" :class="{ 'sidebar-is-collapsed': sidebarCollapsed }">
      <aside class="sidebar">
        <div class="sidebar-head"><span class="eyebrow">WORKSPACE</span><button class="icon-button" title="新建集合" aria-label="新建集合" @click="createCollection"><svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.5v11M2.5 8h11" /></svg></button></div>
        <div class="sidebar-tabs"><button :class="{ active: sidebarTab === 'collections' }" @click="sidebarTab = 'collections'">集合</button><button :class="{ active: sidebarTab === 'history' }" @click="sidebarTab = 'history'">历史</button></div>
        <div v-if="sidebarTab === 'collections'" class="sidebar-scroll">
          <div v-if="!workspace.collections.length" class="empty-sidebar">还没有集合。<button @click="createCollection">创建第一个集合 →</button></div>
          <div v-for="collection in workspace.collections" :key="collection.id" class="collection-block">
            <div class="collection-line" :class="{ selected: selectedCollectionId === collection.id }">
              <span class="folder-icon">▣</span><span class="collection-name" @click="selectedCollectionId = collection.id">{{ collection.name }}</span>
              <button class="tiny-button" title="新建请求" @click="createRequest(collection.id)">＋</button>
              <button class="tiny-button more-button" title="重命名" @click="renameCollection(collection)">⋯</button>
            </div>
            <div v-for="folder in collection.folders" :key="folder.id" class="folder-group">
              <div class="folder-line"><span>⌄</span><span>{{ folder.name }}</span><button class="tiny-button" title="在文件夹中新建请求" @click="createRequest(collection.id, folder.id)">＋</button></div>
              <button v-for="request in collection.requests.filter(x => x.folderId === folder.id)" :key="request.id" class="request-line nested" :class="{ active: selectedRequestId === request.id }" @click="openRequest(collection, request)"><span :class="['method-mini', methodClass(request.method)]">{{ request.method }}</span><span class="truncate">{{ request.name }}</span></button>
            </div>
            <button v-for="request in collection.requests.filter(x => !x.folderId)" :key="request.id" class="request-line" :class="{ active: selectedRequestId === request.id }" @click="openRequest(collection, request)"><span :class="['method-mini', methodClass(request.method)]">{{ request.method }}</span><span class="truncate">{{ request.name }}</span></button>
            <div v-if="selectedCollectionId === collection.id" class="collection-actions"><button @click="createFolder(collection)">＋ 文件夹</button><button @click="deleteCollection(collection)">删除集合</button></div>
            <div v-if="folderCreationCollectionId === collection.id" class="folder-create"><input v-model="newFolderName" aria-label="文件夹名称" placeholder="文件夹名称" @keydown.enter="commitFolder(collection)" /><button @click="commitFolder(collection)">保存</button><button @click="folderCreationCollectionId = ''">取消</button></div>
          </div>
        </div>
        <div v-else class="sidebar-scroll history-list"><div v-if="!workspace.history.length" class="empty-sidebar">发送请求后会显示在这里。</div><button v-for="item in workspace.history" :key="item.id" class="history-line" :title="`${new Date(item.at).toLocaleString()} · ${item.status}`" @click="openHistory(item)"><span :class="['method-mini', methodClass(item.method)]">{{ item.method }}</span><span class="truncate">{{ item.request?.name || item.url }}</span><small>{{ item.status }}</small></button></div>
        <div class="sidebar-footer"><span class="footer-dot"></span> SQLite 本地保存 <span class="save-state">{{ saving ? '保存中…' : '已就绪' }}</span></div>
      </aside>

      <main class="main-panel">
        <div class="editor-header">
          <div class="editor-identity"><input v-model="draft.name" class="request-title" aria-label="请求名称" /><div class="category-picker"><span>保存到</span><select v-model="selectedCollectionId" aria-label="选择集合" @change="onCollectionChanged"><option value="">新建集合</option><option v-for="collection in workspace.collections" :key="collection.id" :value="collection.id">{{ collection.name }}</option></select><select v-if="selectedCollection" v-model="draft.folderId" aria-label="选择文件夹"><option value="">集合根目录</option><option v-for="folder in selectedCollection.folders" :key="folder.id" :value="folder.id">{{ folder.name }}</option></select></div></div>
          <div class="header-actions"><button class="subtle-button" @click="deleteRequest" :disabled="!selectedRequestId">删除</button><button class="outline-button" @click="saveRequest">保存请求</button></div>
        </div>
        <section class="composer">
          <div class="section-label"><span class="section-index">01</span> REQUEST BUILDER <span class="section-line"></span><span class="muted">配置并发送 HTTP 请求</span></div>
          <div class="url-bar"><select v-model="draft.method" aria-label="HTTP 方法" :class="methodClass(draft.method)"><option>GET</option><option>POST</option><option>PUT</option><option>PATCH</option><option>DELETE</option><option>HEAD</option><option>OPTIONS</option></select><input v-model="draft.url" type="url" placeholder="https://api.example.com/v1/users" aria-label="请求 URL" @keydown.enter="sendRequest" /><button v-if="busy" class="send-button cancel" @click="cancelRequest">取消</button><button v-else class="send-button" :disabled="!draft.url.trim()" @click="sendRequest">发送请求 <span>↗</span></button></div>
          <label class="ssl-option" :class="{ unsafe: draft.allowInsecureSsl }"><input v-model="draft.allowInsecureSsl" type="checkbox" /><span class="ssl-checkbox" aria-hidden="true">✓</span><span><strong>允许不安全的 SSL 连接</strong><small>用于自签名或证书不受信任的 HTTPS 服务</small></span><span v-if="draft.allowInsecureSsl" class="unsafe-badge">已跳过证书验证</span></label>
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

            <template v-else><div class="pane-intro"><h3>请求正文</h3><p>选择正文格式。GET 和 HEAD 请求不会发送正文。</p></div><div class="body-options"><label v-for="option in [{ value: 'none', label: 'None' }, { value: 'json', label: 'JSON' }, { value: 'text', label: 'Text' }, { value: 'form', label: 'Form URL Encoded' }, { value: 'multipart', label: 'Multipart' }]" :key="option.value"><input v-model="draft.bodyType" type="radio" :value="option.value" />{{ option.label }}</label></div><textarea v-if="draft.bodyType === 'json' || draft.bodyType === 'text'" v-model="draft.body" class="body-editor" spellcheck="false" :placeholder="draft.bodyType === 'json' ? '{\n  &quot;name&quot;: &quot;example&quot;\n}' : '在这里输入请求正文…'"></textarea><template v-else-if="draft.bodyType === 'form' || draft.bodyType === 'multipart'"><div class="kv-head"><span>启用</span><span>KEY</span><span>VALUE</span><span></span></div><div v-for="row in draft.form" :key="row.id" class="kv-row"><input v-model="row.enabled" type="checkbox" /><input v-model="row.key" placeholder="Key" /><input v-model="row.value" placeholder="Value" /><button class="remove-row" @click="draft.form = draft.form.filter(x => x.id !== row.id)">×</button></div><button class="add-row" @click="draft.form.push(newRow())">＋ 添加字段</button><div v-if="draft.bodyType === 'multipart'" class="file-box"><label class="outline-button upload-label">＋ 添加文件<input type="file" multiple hidden @change="addFiles" /></label><span class="muted">每个文件最大 10 MB；文件只用于本次发送</span><div v-for="(file, index) in files" :key="index" class="file-row"><input v-model="file.key" aria-label="文件字段名" /><span>{{ file.fileName }}</span><button @click="files.splice(index, 1)">×</button></div></div></template></template>
          </div>
        </section>

        <section class="response-section">
          <div class="section-label"><span class="section-index">02</span> RESPONSE <span class="section-line"></span><span v-if="response" class="response-metrics"><span :class="response.status < 400 ? 'status-good' : 'status-bad'">{{ response.status }} {{ response.statusText }}</span><span>{{ response.durationMs }} ms</span><span>{{ (response.size / 1024).toFixed(1) }} KB</span></span></div>
          <div v-if="error" class="error-banner">{{ error }}</div>
          <div v-if="response" class="response-card">
            <div class="response-tabs"><button :class="{ active: responseTab === 'body' }" @click="responseTab = 'body'">Body</button><button :class="{ active: responseTab === 'headers' }" @click="responseTab = 'headers'">Headers <span>{{ Object.keys(response.headers).length }}</span></button><button :class="{ active: responseTab === 'cookies' }" @click="responseTab = 'cookies'">Cookies <span>{{ response.cookies.length }}</span></button><button :class="{ active: responseTab === 'trace' }" @click="responseTab = 'trace'">请求诊断</button><span class="content-type">{{ response.contentType || 'text/plain' }}</span></div>
            <pre v-if="responseTab === 'body'" class="response-body">{{ formattedBody }}</pre>
            <div v-else-if="responseTab === 'headers'" class="response-headers"><div v-for="(value, key) in response.headers" :key="key"><strong>{{ key }}</strong><span>{{ value }}</span></div></div>
            <div v-else-if="responseTab === 'cookies'" class="response-cookies"><p v-if="!response.cookies.length" class="muted">响应中没有 Set-Cookie。请求 Cookie 可在上方 Cookies 标签设置。</p><div v-for="(cookie, index) in response.cookies" :key="index" class="cookie-card"><strong>{{ cookie.name }}</strong><span>{{ cookie.value }}</span><small>{{ cookie.attributes || '无附加属性' }}</small></div></div>
            <div v-else class="trace-panel"><div class="trace-summary"><strong>{{ response.trace.method }}</strong><code>{{ response.trace.url }}</code><span>{{ response.trace.httpVersion }}</span></div><h4>阶段耗时</h4><div v-for="stage in response.trace.stages" :key="stage.name" class="trace-stage"><span>{{ stage.name }}</span><div class="trace-track"><i :style="{ width: `${Math.max(3, Math.round(stage.durationMs / Math.max(response.durationMs, 1) * 100))}%` }"></i></div><b>{{ stage.durationMs }} ms</b><small>{{ stage.detail }}</small></div><p class="trace-hint">等待响应头包含 DNS、TCP、TLS 和服务端处理；连接复用或重定向时无法分别精确计时。</p><h4>实际请求头 <small>敏感字段已隐藏</small></h4><div class="trace-headers"><div v-for="(value, key) in response.trace.requestHeaders" :key="key"><code>{{ key }}</code><code>{{ value }}</code></div></div><h4 v-if="response.trace.requestBody">请求正文预览</h4><pre v-if="response.trace.requestBody" class="trace-body">{{ response.trace.requestBody }}</pre></div>
            <div v-if="response.truncated" class="truncated-note">响应超过 2 MB，只显示前 2 MB。</div>
          </div>
          <div v-else-if="!error" class="response-empty"><div class="response-empty-icon">↗</div><strong>响应将显示在这里</strong><span>输入 URL 并发送请求，查看状态、耗时、响应头与正文。</span></div>
        </section>
      </main>
    </div>

    <div v-if="showEnvironments" class="modal-backdrop" @click.self="showEnvironments = false"><div class="env-modal"><div class="modal-head"><div><span class="eyebrow">WORKSPACE SETTINGS</span><h2>环境变量</h2></div><button class="close-button" @click="showEnvironments = false">×</button></div><p class="modal-description">在 URL、请求头、正文及验证信息中使用 <code v-pre>{{变量名}}</code>。</p><div class="env-toolbar"><select v-model="activeEnvironmentId"><option value="">不使用环境</option><option v-for="environment in workspace.environments" :key="environment.id" :value="environment.id">{{ environment.name }}</option></select><button class="outline-button" @click="addEnvironment">＋ 新建环境</button></div><template v-if="activeEnvironment"><div class="form-field env-name"><label>环境名称</label><input v-model="activeEnvironment.name" /></div><div class="kv-head"><span>启用</span><span>KEY</span><span>VALUE</span><span></span></div><div v-for="row in activeEnvironment.variables" :key="row.id" class="kv-row"><input v-model="row.enabled" type="checkbox" /><input v-model="row.key" placeholder="base_url" /><input v-model="row.value" type="password" placeholder="变量值" /><button class="remove-row" @click="activeEnvironment!.variables = activeEnvironment!.variables.filter(x => x.id !== row.id)">×</button></div><button class="add-row" @click="activeEnvironment!.variables.push(newRow())">＋ 添加变量</button></template><div class="modal-actions"><button v-if="activeEnvironment" class="subtle-button" @click="workspace.environments = workspace.environments.filter(x => x.id !== activeEnvironmentId); activeEnvironmentId = ''; persist()">删除环境</button><button class="send-button" @click="persist(); showEnvironments = false">保存并关闭</button></div></div></div>
    <div v-if="notice" class="toast">✓ {{ notice }}</div>
  </div>
</template>
