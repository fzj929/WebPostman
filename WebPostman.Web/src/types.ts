export type Row = { id: string; key: string; value: string; enabled: boolean }
export type Auth = { type: 'inherit' | 'none' | 'basic' | 'bearer' | 'apiKey'; username: string; password: string; token: string; key: string; value: string; in: 'header' | 'query' }
export type UploadFile = { key: string; fileName: string; contentType: string; base64: string }
export type RequestItem = { id: string; name: string; method: string; url: string; params: Row[]; headers: Row[]; cookies?: Row[]; bodyType: string; body: string; form: Row[]; auth: Auth; folderId: string; files?: UploadFile[]; allowInsecureSsl: boolean; timeoutSeconds?: number; followRedirects?: boolean; showSensitiveTrace?: boolean; fileFields?: { key: string; fileName: string }[] }
export type Folder = { id: string; name: string; auth: Auth; parentId?: string }
export type Collection = { id: string; name: string; auth: Auth; folders: Folder[]; requests: RequestItem[] }
export type Environment = { id: string; name: string; variables: Row[] }
export type History = { id: string; at: string; method: string; url: string; status: number; durationMs: number; request?: RequestItem; effectiveAuth?: Auth; variables?: Record<string, string>; collectionId?: string }
export type Workspace = { collections: Collection[]; environments: Environment[]; history: History[]; revision?: number }
export type CookieInfo = { name: string; value: string; attributes: string; raw: string }
export type TraceStage = { name: string; durationMs: number; detail: string }
export type RequestTrace = { url: string; originalUrl?: string; method: string; httpVersion: string; requestHeaders: Record<string, string>; requestBody: string; stages: TraceStage[] }
export type ExecuteResult = { status: number; statusText: string; durationMs: number; size: number; contentType: string; headers: Record<string, string>; body: string; truncated: boolean; cookies: CookieInfo[]; trace: RequestTrace; bodyBase64?: string; recordedBodyBase64?: string; fileName?: string; history?: History }
export type UserInfo = { id: string; email: string }
export type ShareSummary = { id: string; ownerId: string; author: string; kind: 'request' | 'collection'; title: string; description: string; createdAt: string; requestCount: number }
export type SharedItem = ShareSummary & { collection: Collection }

export const uid = (): string => {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  // Plain HTTP on LAN hosts does not expose randomUUID; getRandomValues still works.
  const bytes = crypto.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6]! & 0x0f) | 0x40
  bytes[8] = (bytes[8]! & 0x3f) | 0x80
  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}
export const newRow = (): Row => ({ id: uid(), key: '', value: '', enabled: true })
export const newAuth = (type: Auth['type'] = 'none'): Auth => ({ type, username: '', password: '', token: '', key: '', value: '', in: 'header' })
export const newRequest = (folderId = ''): RequestItem => ({ id: uid(), name: '未命名请求', method: 'GET', url: '', params: [newRow()], headers: [newRow()], cookies: [newRow()], bodyType: 'none', body: '', form: [newRow()], auth: newAuth('inherit'), folderId, allowInsecureSsl: false, timeoutSeconds: 60, followRedirects: true, showSensitiveTrace: false })
