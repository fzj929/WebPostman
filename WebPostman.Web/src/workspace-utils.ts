import type { Auth, Collection, Environment, Folder, RequestItem, Row, Workspace } from './types'
import { newAuth, newRequest, uid } from './types'

const object = (value: unknown): Record<string, any> => {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('导入文件包含无效对象')
  return value as Record<string, any>
}
const array = (value: unknown, limit: number): any[] => {
  if (!Array.isArray(value) || value.length > limit) throw new Error('导入文件缺少列表或数量超过限制')
  return value
}
const text = (value: unknown, fallback = '', limit = 1000000): string => {
  if (value === undefined) return fallback
  if (typeof value !== 'string' || value.length > limit) throw new Error('导入文件包含无效文本字段')
  return value
}
function rows(value: unknown): Row[] {
  return array(value ?? [], 1000).map(value => { const x = object(value); return { id: uid(), key: text(x.key, '', 8192), value: text(x.value), enabled: x.enabled !== false } })
}
function auth(value: unknown, inherit = true): Auth {
  if (value === undefined) return newAuth(inherit ? 'inherit' : 'none')
  const x = object(value)
  if (!['none', 'basic', 'bearer', 'apiKey', ...(inherit ? ['inherit'] : [])].includes(x.type)) throw new Error('导入文件包含不支持的授权类型')
  return { type: x.type, username: text(x.username), password: text(x.password), token: text(x.token), key: text(x.key), value: text(x.value), in: x.in === 'query' ? 'query' : 'header' }
}
export function resolveAuth(request: RequestItem, collection?: Collection): Auth {
  if (request.auth.type !== 'inherit') return request.auth
  let folder = collection?.folders.find(x => x.id === request.folderId)
  const visited = new Set<string>()
  while (folder && !visited.has(folder.id)) {
    visited.add(folder.id)
    if (folder.auth.type !== 'inherit') return folder.auth
    folder = collection?.folders.find(x => x.id === folder!.parentId)
  }
  return collection?.auth ?? newAuth()
}
export function folderPath(collection: Collection, folder: Folder): string {
  const names = [folder.name]; const seen = new Set([folder.id]); let parent = folder.parentId
  while (parent && !seen.has(parent)) {
    seen.add(parent); const item = collection.folders.find(x => x.id === parent)
    if (!item) break
    names.unshift(item.name); parent = item.parentId
  }
  return names.join(' / ')
}
function postmanAuth(value: unknown, inherit = true): Auth {
  if (!value) return newAuth(inherit ? 'inherit' : 'none')
  const source = object(value)
  const get = (key: string) => text(array(source[source.type] ?? [], 100).find(x => x.key === key)?.value)
  if (source.type === 'noauth') return newAuth()
  if (source.type === 'basic') return { ...newAuth('basic'), username: get('username'), password: get('password') }
  if (source.type === 'bearer') return { ...newAuth('bearer'), token: get('token') }
  if (source.type === 'apikey') return { ...newAuth('apiKey'), key: get('key'), value: get('value'), in: get('in') === 'query' ? 'query' : 'header' }
  throw new Error(`暂不支持导入授权类型：${source.type}`)
}
function postmanRows(value: unknown): Row[] {
  return array(value ?? [], 1000).map(value => { const x = object(value); return { id: uid(), key: text(x.key), value: text(x.value), enabled: !x.disabled } })
}
export function parseImport(value: unknown): { collections: Collection[]; environments: Environment[]; warnings: string[] } {
  const data = object(value); const warnings: string[] = []
  if (data.schemaVersion !== undefined && ![1, 2].includes(data.schemaVersion)) throw new Error('导出文件版本不支持，请升级应用后再导入')
  if (data.info && Array.isArray(data.item)) {
    const collection: Collection = { id: uid(), name: text(object(data.info).name, '导入的集合'), auth: postmanAuth(data.auth, false), folders: [], requests: [] }
    let count = 0
    const visit = (items: unknown, parentId = '', depth = 0) => {
      if (depth > 30) throw new Error('文件夹层级超过 30 层')
      for (const value of array(items, 10000)) {
        if (++count > 10000) throw new Error('导入项目过多')
        const item = object(value)
        if (Array.isArray(item.item)) {
          const folder = { id: uid(), name: text(item.name, '文件夹'), auth: postmanAuth(item.auth), parentId }
          collection.folders.push(folder); visit(item.item, folder.id, depth + 1)
        } else if (item.request) {
          const source = typeof item.request === 'string' ? { url: item.request } : object(item.request)
          const request = newRequest(parentId); request.name = text(item.name, request.name)
          request.method = text(source.method, 'GET').toUpperCase()
          if (!['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'].includes(request.method)) throw new Error('导入请求的方法不支持')
          request.url = typeof source.url === 'string' ? source.url : text(source.url?.raw)
          if (source.url?.query?.length) request.url = request.url.split('?')[0]
          request.params = postmanRows(source.url?.query); request.headers = postmanRows(source.header); request.auth = postmanAuth(source.auth)
          const body = source.body ? object(source.body) : {}; const mode = body.mode
          if (mode && !['raw', 'urlencoded', 'formdata'].includes(mode)) throw new Error(`暂不支持正文格式：${mode}`)
          request.bodyType = mode === 'raw' ? (body.options?.raw?.language === 'json' ? 'json' : 'text') : mode === 'urlencoded' ? 'form' : mode === 'formdata' ? 'multipart' : 'none'
          request.body = text(body.raw)
          request.form = postmanRows(mode === 'formdata' ? array(body.formdata ?? [], 1000).filter(x => x.type !== 'file') : body.urlencoded)
          if (mode === 'formdata') {
            request.fileFields = array(body.formdata ?? [], 1000).filter(x => x.type === 'file' && !x.disabled).flatMap(x => (Array.isArray(x.src) ? x.src : [x.src ?? '']).map((src: unknown) => ({ key: text(x.key), fileName: text(src).split(/[\\/]/).pop() ?? '' })))
            if (request.fileFields.length) warnings.push(`「${request.name}」的文件字段需要重新选择本机文件`)
          }
          collection.requests.push(request)
        }
      }
    }
    visit(data.item)
    return { collections: [collection], environments: [], warnings }
  }
  const rawCollections = [...array(data.collections, 500)]
  if (data.unsavedTabs !== undefined) {
    const drafts = array(data.unsavedTabs, 100).map(value => ({ ...object(object(value).request), folderId: '' }))
    if (drafts.length) rawCollections.push({ name: '恢复的未保存草稿', auth: newAuth(), folders: [], requests: drafts })
  }
  const collections = rawCollections.map(value => {
    const x = object(value); const map = new Map<string, string>()
    const folders = array(x.folders, 1000).map(value => {
      const f = object(value); const oldId = text(f.id)
      if (!oldId || map.has(oldId)) throw new Error('文件夹 ID 缺失或重复')
      const id = uid(); map.set(oldId, id)
      return { id, name: text(f.name), auth: auth(f.auth), parentId: text(f.parentId) }
    })
    for (const f of folders) { if (f.parentId && !map.has(f.parentId)) throw new Error('上级文件夹不存在'); f.parentId = map.get(f.parentId) ?? '' }
    for (const f of folders) {
      let parent = f.parentId; const seen = new Set<string>([f.id])
      while (parent) { if (seen.has(parent)) throw new Error('文件夹层级存在循环'); if (seen.size >= 30) throw new Error('文件夹层级超过 30 层'); seen.add(parent); parent = folders.find(x => x.id === parent)?.parentId ?? '' }
    }
    const requests = array(x.requests, 10000).map(value => {
      const r = object(value); const method = text(r.method, 'GET').toUpperCase()
      if (!['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'].includes(method)) throw new Error('HTTP 方法无效')
      if (!['none', 'json', 'text', 'form', 'multipart'].includes(r.bodyType)) throw new Error('请求正文类型无效')
      const folder = text(r.folderId); if (folder && !map.has(folder)) throw new Error('请求所在文件夹不存在')
      const timeout = r.timeoutSeconds ?? 60
      if (!Number.isInteger(timeout) || timeout < 1 || timeout > 600) throw new Error('超时配置无效')
      return { ...newRequest(map.get(folder) ?? ''), name: text(r.name), method, url: text(r.url), params: rows(r.params), headers: rows(r.headers), cookies: rows(r.cookies), bodyType: r.bodyType, body: text(r.body), form: rows(r.form), auth: auth(r.auth), allowInsecureSsl: r.allowInsecureSsl === true, timeoutSeconds: timeout, followRedirects: r.followRedirects !== false, showSensitiveTrace: false, fileFields: array(r.fileFields ?? [], 100).map(value => { const f = object(value); return { key: text(f.key), fileName: text(f.fileName) } }) }
    })
    return { id: uid(), name: text(x.name), auth: auth(x.auth, false), folders, requests }
  })
  const environments = array(data.environments, 100).map(value => { const x = object(value); return { id: uid(), name: text(x.name), variables: rows(x.variables) } })
  return { collections, environments, warnings }
}

export const expandVariables = (value: string, variables: Record<string, string>) => value.replace(/\{\{\s*([^{}]+?)\s*\}\}/g, (match, name: string) => variables[name] ?? match)
export function curlCommand(request: RequestItem, auth: Auth, variables: Record<string, string>, files: { key: string; fileName: string }[] = []): string {
  const expand = (v: string) => expandVariables(v, variables)
  const quote = (v: string) => "'" + v.replace(/'/g, "'\\''") + "'"
  let url = expand(request.url)
  const params = request.params.filter(x => x.enabled && x.key).map(x => [expand(x.key), expand(x.value)])
  if (auth.type === 'apiKey' && auth.in === 'query') params.push([expand(auth.key), expand(auth.value)])
  if (params.length) { const hash = url.indexOf('#'); const fragment = hash < 0 ? '' : url.slice(hash); if (hash >= 0) url = url.slice(0, hash); url += (url.includes('?') ? '&' : '?') + new URLSearchParams(params).toString() + fragment }
  const result = ['curl', '-X', request.method.toUpperCase(), '--max-time', String(request.timeoutSeconds ?? 60)]
  if (request.followRedirects !== false) result.push('--location', '--max-redirs', '10')
  if (request.allowInsecureSsl) result.push('--insecure')
  const headers = request.headers.filter(x => x.enabled && x.key && !(auth.type !== 'none' && x.key.toLowerCase() === 'authorization')).map(x => [expand(x.key), expand(x.value)])
  if (auth.type === 'basic') result.push('--user', quote(`${expand(auth.username)}:${expand(auth.password)}`))
  if (auth.type === 'bearer') headers.push(['Authorization', `Bearer ${expand(auth.token)}`])
  if (auth.type === 'apiKey' && auth.in === 'header') { for (let i = headers.length - 1; i >= 0; i--) if (headers[i][0]?.toLowerCase() === expand(auth.key).toLowerCase()) headers.splice(i, 1); headers.push([expand(auth.key), expand(auth.value)]) }
  const cookies = (request.cookies ?? []).filter(x => x.enabled && x.key).map(x => `${expand(x.key)}=${expand(x.value)}`)
  if (cookies.length) result.push('--cookie', quote(cookies.join('; ')))
  if (!['GET', 'HEAD'].includes(request.method.toUpperCase())) {
    if (['json', 'text'].includes(request.bodyType)) { if (!headers.some(x => x[0]?.toLowerCase() === 'content-type')) headers.push(['Content-Type', request.bodyType === 'json' ? 'application/json' : 'text/plain']); result.push('--data-raw', quote(expand(request.body))) }
    if (request.bodyType === 'form') for (const row of request.form.filter(x => x.enabled && x.key)) result.push('--data-urlencode', quote(`${expand(row.key)}=${expand(row.value)}`))
    if (request.bodyType === 'multipart') { for (const row of request.form.filter(x => x.enabled && x.key)) result.push('--form-string', quote(`${expand(row.key)}=${expand(row.value)}`)); for (const file of files) result.push('--form', quote(`${expand(file.key)}=@${file.fileName}`)) }
  }
  for (const [key, value] of headers) result.push('--header', quote(`${key}: ${value}`))
  result.push(quote(url)); return result.join(' ')
}

export function workspaceChanges(before: Workspace, after: Workspace) {
  const changed = <T extends { id: string }>(a: T[], b: T[]) => { const previous = new Map(a.map(x => [x.id, JSON.stringify(x)])); return b.filter(x => JSON.stringify(x) !== previous.get(x.id)) }
  return { revision: before.revision ?? 0, collections: changed(before.collections, after.collections), deletedCollections: before.collections.filter(x => !after.collections.some(y => y.id === x.id)).map(x => x.id), environments: changed(before.environments, after.environments), deletedEnvironments: before.environments.filter(x => !after.environments.some(y => y.id === x.id)).map(x => x.id) }
}
