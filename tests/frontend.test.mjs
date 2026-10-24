import test from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import ts from '../WebPostman.Web/node_modules/typescript/lib/typescript.js'
import sfc from '../WebPostman.Web/node_modules/@vue/compiler-sfc/dist/compiler-sfc.cjs.js'

// Compile the same pure modules consumed by Vue, without adding a test runtime.
const compile = source => ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText
const dataUrl = source => `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`
const typesUrl = dataUrl(compile(await readFile(new URL('../WebPostman.Web/src/types.ts', import.meta.url), 'utf8')))
const utilsSource = compile(await readFile(new URL('../WebPostman.Web/src/workspace-utils.ts', import.meta.url), 'utf8')).replaceAll("from './types'", `from '${typesUrl}'`)
const utilsUrl = dataUrl(utilsSource)
const { parseImport, resolveAuth, folderPath, curlCommand, workspaceChanges } = await import(utilsUrl)
const { newAuth, newRequest } = await import(typesUrl)
const vueUrl = new URL('../WebPostman.Web/node_modules/vue/index.mjs', import.meta.url).href
const lifecycleAdapter = dataUrl(`export * from '${vueUrl}'; export const onMounted = () => {}; export const onBeforeUnmount = () => {};`)
const descriptor = sfc.parse(await readFile(new URL('../WebPostman.Web/src/App.vue', import.meta.url), 'utf8')).descriptor
const appSource = compile(sfc.compileScript(descriptor, { id: 'regression' }).content)
  .replaceAll("from 'vue'", `from '${lifecycleAdapter}'`).replaceAll("from './types'", `from '${typesUrl}'`).replaceAll("from './workspace-utils'", `from '${utilsUrl}'`)
const App = (await import(dataUrl(appSource))).default
const createEditor = () => App.setup({}, { expose() {} })
const jsonResponse = (data, status = 200) => ({ ok: status < 400, status, json: async () => data })
const result = name => ({ body: name, contentType: 'text/plain', status: 200 })

test('nested Postman folders preserve auth inheritance and multipart fields', () => {
  const imported = parseImport({ info: { name: 'Imported' }, item: [{ name: 'Parent', auth: { type: 'bearer', bearer: [{ key: 'token', value: '{{token}}' }] }, item: [{ name: 'Child', item: [{ name: 'Upload', request: { method: 'POST', url: 'https://example.test', body: { mode: 'formdata', formdata: [{ key: 'name', value: 'value' }, { key: 'upload', type: 'file', src: '/private/file.txt' }] } } }] }] }] })
  const c = imported.collections[0], r = c.requests[0]
  assert.equal(folderPath(c, c.folders[1]), 'Parent / Child')
  assert.equal(resolveAuth(r, c).token, '{{token}}')
  assert.equal(r.bodyType, 'multipart'); assert.equal(r.form[0].value, 'value')
  assert.deepEqual(r.fileFields, [{ key: 'upload', fileName: 'file.txt' }])
  assert.equal(imported.warnings.length, 1)
})
test('native imports remap every entity ID and refuse malformed/cyclic input', () => {
  const source = { collections: [{ id: 'c', name: 'C', auth: newAuth(), folders: [{ id: 'f', name: 'F', auth: newAuth('inherit') }], requests: [{ ...newRequest('f'), id: 'r' }] }], environments: [{ id: 'e', name: 'E', variables: [] }] }
  const a = parseImport(source), b = parseImport(source)
  assert.notEqual(a.collections[0].id, b.collections[0].id)
  assert.notEqual(a.collections[0].requests[0].id, b.collections[0].requests[0].id)
  assert.equal(a.collections[0].requests[0].folderId, a.collections[0].folders[0].id)
  source.collections[0].folders[0].parentId = 'f'
  assert.throws(() => parseImport(source), /循环/)
  assert.throws(() => parseImport({ collections: [null], environments: [] }))
  assert.throws(() => parseImport({ collections: [], environments: [{ variables: null, name: 42 }] }))
})
test('cURL encodes params, resolves auth, escapes shell literals and preserves file fields', () => {
  const r = { ...newRequest(), method: 'POST', url: '{{base}}/upload#section', bodyType: 'multipart', params: [{ key: 'q', value: 'a b', enabled: true }], form: [{ key: 'name', value: "O'Brien", enabled: true }] }
  const command = curlCommand(r, { ...newAuth('bearer'), token: '{{token}}' }, { base: 'https://example.test', token: 'demo' }, [{ key: 'document', fileName: 'sample.txt' }])
  assert.ok(command.includes('q=a+b#section'))
  assert.ok(command.includes('Bearer demo')); assert.ok(command.includes('document=@sample.txt'))
  assert.ok(command.includes("O'\\''Brien")); assert.ok(command.includes('--form-string'))
})
test('workspace diff sends changed entities only and includes optimistic revision', () => {
  const before = { collections: [{ id: 'a', name: 'A' }, { id: 'b', name: 'B' }], environments: [], revision: 4 }
  const after = { collections: [{ id: 'a', name: 'changed' }], environments: [], revision: 4 }
  assert.deepEqual(workspaceChanges(before, after), { revision: 4, collections: [{ id: 'a', name: 'changed' }], deletedCollections: ['b'], environments: [], deletedEnvironments: [] })
})
test('exported unsaved drafts can be recovered as new requests', () => {
  const result = parseImport({ collections: [], environments: [], unsavedTabs: [{ collectionId: 'deleted', request: { ...newRequest('old-folder'), name: 'Unsaved' } }] })
  assert.equal(result.collections[0].name, '恢复的未保存草稿')
  assert.equal(result.collections[0].requests[0].name, 'Unsaved')
  assert.equal(result.collections[0].requests[0].folderId, '')
})
test('actual Vue editor guards duplicate sends and isolates out-of-order tab responses', async () => {
  const originalFetch = globalThis.fetch
  const pending = []
  globalThis.fetch = async (url, options) => {
    if (url.startsWith('/api/history')) return jsonResponse({ items: [], total: 0 })
    return new Promise(resolve => pending.push({ resolve, options }))
  }
  try {
    const vm = createEditor(); vm.createRequest(); vm.draft.value.url = 'https://example.test/slow'
    const slowTab = vm.editorTabs.value[0]; const slow = vm.sendRequest()
    await vm.sendRequest(); assert.equal(pending.length, 1)
    vm.createRequest(); vm.draft.value.url = 'https://example.test/fast'
    const fast = vm.sendRequest(); assert.equal(pending.length, 2)
    pending[1].resolve(jsonResponse(result('fast'))); await fast; assert.equal(vm.response.value.body, 'fast')
    pending[0].resolve(jsonResponse(result('slow'))); await slow; assert.equal(vm.response.value.body, 'fast')
    vm.activateEditor(slowTab); assert.equal(vm.response.value.body, 'slow'); assert.equal(vm.busy.value, false)
  } finally { globalThis.fetch = originalFetch }
})
test('actual Vue cancellation resets busy state without replacing another tab', async () => {
  const originalFetch = globalThis.fetch
  globalThis.fetch = async (_url, options) => new Promise((_resolve, reject) => options.signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError'))))
  try {
    const vm = createEditor(); vm.createRequest(); vm.draft.value.url = 'https://example.test/slow'
    const sending = vm.sendRequest(); vm.cancelRequest(); await sending
    assert.equal(vm.busy.value, false); assert.equal(vm.error.value, '请求已取消')
  } finally { globalThis.fetch = originalFetch }
})
test('failed import retains editable data, reports conflict, and never announces completion', async () => {
  const originalFetch = globalThis.fetch
  globalThis.fetch = async () => jsonResponse({ error: 'Conflict' }, 409)
  try {
    const vm = createEditor(); vm.createRequest()
    const input = { files: [{ size: 100, text: async () => JSON.stringify({ collections: [{ name: 'Imported', auth: newAuth(), folders: [], requests: [] }], environments: [] }) }], value: 'chosen.json' }
    await vm.importFile({ target: input })
    assert.equal(vm.workspace.value.collections[0].name, 'Imported')
    assert.equal(vm.conflict.value, true); assert.equal(vm.notice.value, '')
    assert.ok(vm.saveError.value.includes('保留')); assert.equal(input.value, '')
  } finally { globalThis.fetch = originalFetch }
})
test('successful incremental save clears dirty status and sends no history payload', async () => {
  const originalFetch = globalThis.fetch; let payload
  globalThis.fetch = async (_url, options) => { payload = JSON.parse(options.body); return jsonResponse({ revision: 1 }) }
  try {
    const vm = createEditor(); vm.workspace.value.environments.push({ id: 'env', name: 'Env', variables: [] })
    assert.equal(vm.workspaceDirty.value, true); assert.equal(await vm.persist(), true)
    assert.equal(vm.workspaceDirty.value, false); assert.equal(payload.history, undefined)
    assert.equal(payload.environments.length, 1)
  } finally { globalThis.fetch = originalFetch }
})
test('unsaved editor close can be cancelled and the draft remains intact', () => {
  const originalConfirm = globalThis.confirm
  globalThis.confirm = () => false
  try {
    const vm = createEditor(); vm.createRequest(); vm.draft.value.name = 'Do not lose this'
    vm.closeEditor(vm.editorTabs.value[0])
    assert.equal(vm.editorTabs.value.length, 1); assert.equal(vm.draft.value.name, 'Do not lose this')
    let prevented = false
    vm.beforeUnload({ preventDefault() { prevented = true }, returnValue: undefined })
    assert.equal(prevented, true)
  } finally { globalThis.confirm = originalConfirm }
})
