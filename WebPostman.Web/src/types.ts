export type Row = { id: string; key: string; value: string; enabled: boolean }
export type Auth = { type: 'inherit' | 'none' | 'basic' | 'bearer' | 'apiKey'; username: string; password: string; token: string; key: string; value: string; in: 'header' | 'query' }
export type UploadFile = { key: string; fileName: string; contentType: string; base64: string }
export type RequestItem = { id: string; name: string; method: string; url: string; params: Row[]; headers: Row[]; cookies?: Row[]; bodyType: string; body: string; form: Row[]; auth: Auth; folderId: string; files?: UploadFile[]; allowInsecureSsl: boolean }
export type Folder = { id: string; name: string; auth: Auth }
export type Collection = { id: string; name: string; auth: Auth; folders: Folder[]; requests: RequestItem[] }
export type Environment = { id: string; name: string; variables: Row[] }
export type History = { id: string; at: string; method: string; url: string; status: number; durationMs: number; request?: RequestItem; effectiveAuth?: Auth; variables?: Record<string, string>; collectionId?: string }
export type Workspace = { collections: Collection[]; environments: Environment[]; history: History[] }
export type CookieInfo = { name: string; value: string; attributes: string; raw: string }
export type TraceStage = { name: string; durationMs: number; detail: string }
export type RequestTrace = { url: string; method: string; httpVersion: string; requestHeaders: Record<string, string>; requestBody: string; stages: TraceStage[] }
export type ExecuteResult = { status: number; statusText: string; durationMs: number; size: number; contentType: string; headers: Record<string, string>; body: string; truncated: boolean; cookies: CookieInfo[]; trace: RequestTrace }

export const uid = () => crypto.randomUUID()
export const newRow = (): Row => ({ id: uid(), key: '', value: '', enabled: true })
export const newAuth = (type: Auth['type'] = 'none'): Auth => ({ type, username: '', password: '', token: '', key: '', value: '', in: 'header' })
export const newRequest = (folderId = ''): RequestItem => ({ id: uid(), name: '未命名请求', method: 'GET', url: '', params: [newRow()], headers: [newRow()], cookies: [newRow()], bodyType: 'none', body: '', form: [newRow()], auth: newAuth('inherit'), folderId, allowInsecureSsl: false })
