<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { UserInfo } from '../types'
import { newRow } from '../types'
import { cloneMock, editedMockBody, mockUrl } from '../mock-utils'
import type { MockCall, MockRule, MockRuleDraft, MockServer } from '../mock-utils'

const props = defineProps<{ user: UserInfo | null; recording: MockRuleDraft | null; targetServerId?: string; request: (url: string, options?: RequestInit) => Promise<Response> }>()
const emit = defineEmits<{ login: []; consumed: []; notice: [message: string] }>()
const servers = ref<MockServer[]>([])
const detail = ref<MockServer | null>(null)
const draft = ref<MockRuleDraft | null>(null)
const ruleId = ref('')
const baseline = ref('')
const serverForm = ref({ name: '前端联调', port: 8088, origins: '*' })
const serverBaseline = ref(JSON.stringify(serverForm.value))
const creating = ref(false)
const pendingRecording = ref<MockRuleDraft | null>(props.recording ? cloneMock(props.recording) : null)
const busy = ref(false)
const error = ref('')
const logs = ref<MockCall[]>([])
const host = ref(window.location.hostname)
const search = ref('')
const ruleSearch = ref('')
const dirty = computed(() => !!draft.value && JSON.stringify(draft.value) !== baseline.value)
const serverDirty = computed(() => JSON.stringify(serverForm.value) !== serverBaseline.value)
const selectedRule = computed(() => detail.value?.rules?.find(x => x.id === ruleId.value))
const bodyText = computed(() => draft.value?.response.binary ? draft.value.response.bodyBase64 ?? '' : draft.value?.response.body ?? '')
const baseUrl = computed(() => detail.value ? mockUrl(host.value, detail.value.port) : '')
function hasUnsaved() { return dirty.value || serverDirty.value || !!pendingRecording.value }
function canLeave(includePending = true) { return !((dirty.value || serverDirty.value || (includePending && !!pendingRecording.value)) && !confirm('模拟配置有未保存修改，仍要离开？')) }
defineExpose({ canLeave, hasUnsaved })
function clearError() { error.value = '' }
async function api<T>(path: string, method = 'GET', value?: unknown): Promise<T> {
  const result = await props.request('/api/mocks' + path, { method, headers: { 'Content-Type': 'application/json' }, body: value === undefined ? undefined : JSON.stringify(value) })
  const raw = await result.text(); const data = raw ? JSON.parse(raw) : null
  if (!result.ok) throw new Error(data?.error || `操作失败 (${result.status})`)
  return data as T
}
async function action(work: () => Promise<void>) {
  if (busy.value) return
  busy.value = true; clearError()
  try { await work() } catch (ex) { error.value = ex instanceof Error ? ex.message : '操作失败' }
  finally { busy.value = false }
}
async function list() { servers.value = await api<MockServer[]>('') }
function setDetail(value: MockServer) {
  detail.value = value; creating.value = false
  serverForm.value = { name: value.name, port: value.port, origins: value.corsOrigins.join('\n') }
  serverBaseline.value = JSON.stringify(serverForm.value)
}
function editRule(rule: MockRule) {
  if (!canLeave(false)) return
  const { id, originalResponse, ...value } = cloneMock(rule)
  void originalResponse
  ruleId.value = id; draft.value = value; baseline.value = JSON.stringify(value); clearError()
}
function newRule() {
  if (!canLeave(false)) return
  ruleId.value = ''; baseline.value = ''
  draft.value = { name: '新建模拟接口', method: 'GET', path: '/', query: '', enabled: true, revision: 0, sourceUrl: '', response: { status: 200, contentType: 'application/json; charset=utf-8', headers: [], body: '{\n  "ok": true\n}', bodyBase64: null, binary: false, delayMs: 0 } }
}
function applyRecording() {
  if (!pendingRecording.value) return
  ruleId.value = ''; draft.value = cloneMock(pendingRecording.value); baseline.value = ''
  pendingRecording.value = null; emit('consumed')
}
async function selectServer(server: MockServer) {
  if (busy.value || !canLeave(false)) return
  await action(() => loadServer(server.id))
}
async function loadServer(id: string) {
  const value = await api<MockServer>('/' + id)
  draft.value = null; ruleId.value = ''; baseline.value = ''; logs.value = []
  setDetail(value)
  if (pendingRecording.value) applyRecording()
  else if (value.rules?.[0]) editRule(value.rules[0])
  logs.value = await api<MockCall[]>('/' + id + '/logs')
}
function createServerForm() {
  if (busy.value || !canLeave(false)) return
  if (draft.value && !ruleId.value && !pendingRecording.value) pendingRecording.value = cloneMock(draft.value)
  creating.value = true; detail.value = null; draft.value = null; ruleId.value = ''; baseline.value = ''
  serverForm.value = { name: '前端联调', port: 8088, origins: '*' }; serverBaseline.value = JSON.stringify(serverForm.value); clearError()
}
async function saveServer() {
  await action(async () => {
    const id = detail.value?.id
    const value = await api<MockServer>(id ? '/' + id : '', id ? 'PUT' : 'POST', { name: serverForm.value.name, port: serverForm.value.port, corsOrigins: serverForm.value.origins.split(/\n/).map(x => x.trim()).filter(Boolean), revision: detail.value?.revision ?? 0 })
    setDetail(value); await list(); if (pendingRecording.value) applyRecording()
    emit('notice', id ? '服务配置已保存' : '模拟服务已创建，请保存接口后启动')
  })
}
async function saveRule() {
  if (!detail.value || !draft.value) return
  if (serverDirty.value) { error.value = '请先保存服务配置，再保存接口'; return }
  await action(async () => {
    const serverId = detail.value!.id, savedId = ruleId.value
    const value = await api<MockServer>(`/${serverId}/rules${savedId ? '/' + savedId : ''}`, savedId ? 'PUT' : 'POST', draft.value)
    const previousIds = new Set(detail.value!.rules?.map(x => x.id))
    const current = savedId ? value.rules?.find(x => x.id === savedId) : value.rules?.find(x => !previousIds.has(x.id))
    setDetail(value)
    baseline.value = JSON.stringify(draft.value)
    if (current) {
      const { id, originalResponse, ...data } = cloneMock(current); void originalResponse
      ruleId.value = id; draft.value = data; baseline.value = JSON.stringify(data)
    }
    await list(); emit('notice', value.running ? '模拟响应已更新，下一次请求立即生效' : '模拟接口已保存')
  })
}
async function toggleServer() {
  if (!detail.value || busy.value) return
  if (dirty.value || serverDirty.value) { error.value = '请先保存或放弃当前修改，再启动/停止服务'; return }
  await action(async () => {
    setDetail(await api<MockServer>(`/${detail.value!.id}/${detail.value!.running ? 'stop' : 'start'}`, 'POST'))
    await list(); logs.value = []; emit('notice', detail.value!.running ? '模拟服务已启动' : '模拟服务已停止，端口已释放')
  })
}
async function removeRule() {
  if (!detail.value || !selectedRule.value || !confirm('删除这个模拟接口？原始快照也会删除。')) return
  await action(async () => {
    const value = await api<MockServer>(`/${detail.value!.id}/rules/${ruleId.value}?revision=${selectedRule.value!.revision}`, 'DELETE')
    draft.value = null; ruleId.value = ''; baseline.value = ''; setDetail(value); await list()
  })
}
async function removeServer() {
  if (!detail.value || !confirm('删除整个模拟服务及其接口？运行中的端口会停止。')) return
  await action(async () => {
    await api('/' + detail.value!.id, 'DELETE'); detail.value = null; draft.value = null; ruleId.value = ''; baseline.value = ''; creating.value = false
    serverBaseline.value = JSON.stringify(serverForm.value); await list(); emit('notice', '模拟服务已删除')
  })
}
async function refresh() {
  if (!canLeave(false)) return
  await action(async () => {
    await list()
    if (detail.value) {
      const currentId = ruleId.value; setDetail(await api<MockServer>('/' + detail.value.id))
      draft.value = null; baseline.value = ''; ruleId.value = ''
      const rule = detail.value!.rules?.find(x => x.id === currentId); if (rule) editRule(rule)
      logs.value = await api<MockCall[]>('/' + detail.value!.id + '/logs')
    }
  })
}
function restore() {
  if (!draft.value || !selectedRule.value || !confirm('将响应恢复为录制时的原始内容？保存后生效。')) return
  draft.value.response = cloneMock(selectedRule.value.originalResponse)
}
function changeBody(event: Event) { if (draft.value) draft.value.response = editedMockBody(draft.value.response, (event.target as HTMLTextAreaElement).value) }
function formatBody() {
  if (!draft.value || draft.value.response.binary) return
  try { draft.value.response = editedMockBody(draft.value.response, JSON.stringify(JSON.parse(draft.value.response.body), null, 2)) }
  catch { error.value = '正文不是有效 JSON，可以继续按原始文本保存' }
}
async function copyAddress(path = '', query = '') {
  try { await navigator.clipboard.writeText(mockUrl(host.value, detail.value!.port, path, query)); emit('notice', '模拟地址已复制') }
  catch { error.value = '剪贴板不可用，请手动复制显示的地址' }
}
async function initialize() {
  if (!props.user) return
  if (pendingRecording.value && !props.targetServerId) creating.value = true
  await action(async () => {
    await list()
    if (pendingRecording.value && props.targetServerId) {
      if (!servers.value.some(x => x.id === props.targetServerId)) throw new Error('所选模拟服务已不存在，请从左侧选择其他服务，或新建服务；录制快照仍保留')
      await loadServer(props.targetServerId)
    } else if (pendingRecording.value || !servers.value.length) creating.value = true
  })
}
onMounted(initialize)
</script>

<template>
  <Teleport to="#mock-sidebar">
    <div class="mock-sidebar-content">
      <button class="outline-button" @click="createServerForm" :disabled="!user || busy">＋ 新建模拟服务</button>
      <input v-model="search" aria-label="搜索模拟服务" placeholder="搜索服务名称或端口…" />
      <p v-if="!user" class="muted">登录后创建和管理端口服务。</p>
      <p v-else-if="!servers.length" class="muted">还没有模拟服务。录制真实响应或先创建一个服务。</p>
      <button v-for="server in servers.filter(x => `${x.name} ${x.port}`.toLowerCase().includes(search.toLowerCase()))" :key="server.id" class="mock-server-item" :class="{ selected: detail?.id === server.id }" @click="selectServer(server)" :disabled="busy">
        <span class="mock-state" :class="{ running: server.running }">{{ server.running ? '运行中' : '已停止' }}</span><strong>{{ server.name }}</strong><small>:{{ server.port }} · {{ server.ruleCount }} 个接口</small>
      </button>
      <small class="mock-sidebar-note">每个服务使用独立端口，可容纳多个接口。重启程序后需手动启动。</small>
    </div>
  </Teleport>
  <main class="main-panel mock-panel" :inert="busy">
    <div class="share-panel-heading"><div><span class="eyebrow">MOCK SERVER</span><h1>响应录制与回放</h1><p>保存真实响应，按需修改，让前端联调随时可用。</p></div><button class="outline-button" @click="refresh" :disabled="busy || !user">刷新</button></div>
    <p v-if="error" class="error-banner" role="alert">{{ error }}</p>
    <div v-if="!user" class="share-empty"><strong>登录后创建模拟接口</strong><p>真实请求完成后，点击响应区的“创建模拟接口”。</p><button class="send-button" @click="emit('login')">登录 / 注册</button></div>
    <template v-else>
      <div v-if="pendingRecording" class="mock-recording-note"><strong>响应快照已准备好</strong><span>{{ creating ? '填写新服务的名称和端口，创建后即可保存这次录制的接口。' : '从左侧选择目标服务，或新建服务后保存接口。' }}</span></div>
      <section v-if="creating || detail" class="mock-service-card">
        <div class="mock-card-title"><h2>{{ creating ? '新建模拟服务' : detail?.name }}</h2><div v-if="detail" class="mock-actions"><span class="mock-state" :class="{ running: detail.running }">{{ detail.running ? '运行中' : '已停止' }}</span><button :class="detail.running ? 'outline-button' : 'send-button'" @click="toggleServer" :disabled="busy">{{ detail.running ? '停止服务' : '启动服务' }}</button><button class="subtle-button" @click="removeServer" :disabled="busy">删除服务</button></div></div>
        <form class="mock-service-form" @submit.prevent="saveServer">
          <label>服务名称<input v-model="serverForm.name" aria-label="模拟服务名称" required maxlength="100" /></label>
          <label>监听端口<input v-model.number="serverForm.port" aria-label="模拟监听端口" type="number" min="1024" max="65535" required :disabled="detail?.running" /></label>
          <label class="mock-origins">允许跨域来源（每行一个）<textarea v-model="serverForm.origins" aria-label="模拟跨域来源" rows="2" placeholder="* 表示任意来源；留空关闭跨域" /></label>
          <button class="outline-button" :disabled="busy">{{ creating ? '创建服务' : '保存服务配置' }}</button>
        </form>
        <p class="mock-help">监听服务器所有网络接口；启动后，能访问该端口的人即可获得模拟数据。发布前检查私密内容。跨域不携带浏览器凭据。</p>
        <div v-if="detail" class="mock-address"><label>访问主机<input v-model="host" aria-label="模拟访问主机" placeholder="服务器 IP 或域名" /></label><code>{{ baseUrl }}</code><button class="subtle-button" @click="copyAddress()">复制基础地址</button></div>
        <p v-if="detail" class="mock-help">其他设备请使用服务器 IP，并开放对应端口。HTTPS 页面调用时请通过 HTTPS 反向代理访问。已处理 {{ detail.calls }} 次调用。</p>
      </section>
      <div v-else class="share-empty"><strong>把真实响应变成可编辑的接口</strong><p>从左侧选择服务，或先发送真实请求，在响应区创建模拟接口。</p><button class="outline-button" @click="createServerForm">新建模拟服务</button></div>
      <template v-if="detail">
        <section class="mock-routes-card"><div class="mock-card-title"><h2>模拟接口 <small>{{ detail.ruleCount }}</small></h2><div class="mock-actions"><input v-model="ruleSearch" aria-label="搜索模拟接口" placeholder="搜索名称或路径…" /><button class="outline-button" @click="newRule" :disabled="busy">＋ 手动添加</button></div></div>
          <p v-if="!detail.rules?.length" class="muted">还没有接口。可保存当前录制，或手动添加。</p>
          <div class="mock-routes"><button v-for="rule in detail.rules?.filter(x => `${x.name} ${x.path}`.toLowerCase().includes(ruleSearch.toLowerCase()))" :key="rule.id" class="mock-route" :class="{ selected: ruleId === rule.id, disabled: !rule.enabled }" @click="editRule(rule)" :disabled="busy"><span class="method-mini">{{ rule.method }}</span><strong>{{ rule.name }}</strong><code>{{ rule.path }}{{ rule.query ? '?' + rule.query : '' }}</code><small>{{ rule.response.status }} · {{ rule.enabled ? '启用' : '停用' }}</small></button></div>
        </section>
        <section v-if="draft" class="mock-editor-card">
          <div class="mock-card-title"><h2>{{ ruleId ? '编辑模拟接口' : '保存新的模拟接口' }} <small v-if="dirty">未保存</small></h2><div class="mock-actions"><button v-if="selectedRule" class="subtle-button" @click="restore" :disabled="busy">恢复原始响应</button><button v-if="selectedRule" class="subtle-button" @click="removeRule" :disabled="busy">删除接口</button><button class="send-button" @click="saveRule" :disabled="busy">{{ busy ? '处理中…' : '保存接口' }}</button></div></div>
          <div class="mock-rule-fields"><label>接口名称<input v-model="draft.name" aria-label="模拟接口名称" maxlength="100" /></label><label>请求方法<select v-model="draft.method" aria-label="模拟请求方法"><option v-for="method in ['GET','POST','PUT','PATCH','DELETE','HEAD','OPTIONS']" :key="method">{{ method }}</option></select></label><label class="mock-path">接口路径<input v-model="draft.path" aria-label="模拟接口路径" placeholder="/api/users" maxlength="2048" /></label><label class="mock-check"><input v-model="draft.enabled" type="checkbox" />启用接口</label></div>
          <label class="mock-block-label">查询条件（选填；空表示忽略查询参数）<input v-model="draft.query" aria-label="模拟查询条件" placeholder="page=1&type=demo；填写后按参数和值精确匹配，忽略参数顺序" maxlength="8192" /></label>
          <div class="mock-response-fields"><label>状态码<input v-model.number="draft.response.status" aria-label="模拟响应状态码" type="number" min="200" max="599" /></label><label>Content-Type<input v-model="draft.response.contentType" aria-label="模拟响应类型" maxlength="200" /></label><label>延迟（毫秒）<input v-model.number="draft.response.delayMs" aria-label="模拟延迟毫秒" type="number" min="0" max="30000" /></label></div>
          <details class="mock-headers"><summary>响应头（{{ draft.response.headers.length }}）</summary><div v-for="header in draft.response.headers" :key="header.id" class="kv-row"><input v-model="header.enabled" type="checkbox" aria-label="启用模拟响应头" /><input v-model="header.key" aria-label="模拟响应头名称" placeholder="X-Example" /><input v-model="header.value" aria-label="模拟响应头值" placeholder="Value" /><button class="remove-row" @click="draft.response.headers = draft.response.headers.filter(x => x.id !== header.id)">×</button></div><button class="add-row" @click="draft.response.headers.push(newRow())">＋ 添加响应头</button><p class="mock-help">压缩、长度、连接、Cookie 和跨域响应头由回放服务处理，不会原样重放。</p></details>
          <div class="mock-card-title"><label :for="'mock-response-body'">{{ draft.response.binary ? '二进制响应（Base64）' : '响应正文' }}</label><button v-if="!draft.response.binary" class="subtle-button" @click="formatBody">格式化 JSON</button></div>
          <textarea id="mock-response-body" class="mock-body-editor" :value="bodyText" @input="changeBody" aria-label="模拟响应正文" spellcheck="false" />
          <p v-if="draft.sourceUrl" class="mock-help">录制来源：{{ draft.sourceUrl }}。保存后为独立快照，不随真实接口变化。</p>
          <div class="mock-endpoint"><code>{{ mockUrl(host, detail.port, draft.path, draft.query) }}</code><button class="outline-button" @click="copyAddress(draft.path, draft.query)">复制接口地址</button></div>
          <p class="mock-help">{{ detail.running ? '服务运行中，保存后立即生效。' : '保存后点击上方“启动服务”，即可调用。' }}未匹配的请求返回 404，不转发真实服务。</p>
        </section>
        <section class="mock-logs-card"><div class="mock-card-title"><h2>最近调用</h2><button class="subtle-button" @click="action(async () => { logs = await api<MockCall[]>('/' + detail!.id + '/logs') })" :disabled="busy">刷新调用记录</button></div><p v-if="!logs.length" class="muted">尚无调用记录。记录仅保留当前运行期间最近 50 条。</p><div v-for="(log, index) in logs" :key="index" class="mock-log"><time>{{ new Date(log.at).toLocaleTimeString() }}</time><span>{{ log.method }}</span><code>{{ log.path }}</code><strong>{{ log.status }}</strong><small>{{ log.durationMs }} ms</small></div></section>
      </template>
    </template>
  </main>
</template>
