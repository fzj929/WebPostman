import type { ExecuteResult, Row } from './types'

export type MockResponse = { status: number; contentType: string; headers: Row[]; body: string; bodyBase64: string | null; binary: boolean; delayMs: number }
export type MockRuleDraft = { name: string; method: string; path: string; query: string; enabled: boolean; response: MockResponse; sourceUrl: string; revision: number; truncated?: boolean }
export type MockRule = MockRuleDraft & { id: string; originalResponse: MockResponse }
export type MockServer = { id: string; name: string; port: number; corsOrigins: string[]; revision: number; running: boolean; ruleCount: number; calls: number; rules?: MockRule[] }
export type MockCall = { at: string; method: string; path: string; status: number; durationMs: number }
const removed = /^(content-length|content-encoding|transfer-encoding|connection|keep-alive|te|trailer|upgrade|proxy-authenticate|proxy-authorization|set-cookie|server|date|content-type|access-control-.*)$/i
export const cloneMock = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T
export function recordMock(result: ExecuteResult): MockRuleDraft {
  if (result.truncated) throw new Error('响应已截断，无法创建完整模拟接口；请使用未截断的响应')
  const source = new URL(result.trace.originalUrl || result.trace.url)
  const headers = Object.entries(result.headers)
  const connection = headers.find(([key]) => key.toLowerCase() === 'connection')?.[1].split(',').map(x => x.trim().toLowerCase()) ?? []
  const type = headers.find(([key]) => key.toLowerCase() === 'content-type')?.[1] || result.contentType || 'application/octet-stream'
  return {
    name: (result.history?.request?.name || source.pathname).slice(0, 100), method: result.history?.method || result.trace.method,
    path: source.pathname, query: '', enabled: true, revision: 0, sourceUrl: source.origin + source.pathname,
    response: {
      status: result.status, contentType: type,
      headers: headers.filter(([key]) => !removed.test(key) && !connection.includes(key.toLowerCase())).map(([key, value], index) => ({ id: `recorded-${index}`, key, value, enabled: true })),
      body: result.bodyBase64 != null ? '' : result.body, bodyBase64: result.recordedBodyBase64 ?? result.bodyBase64 ?? null,
      binary: result.bodyBase64 != null, delayMs: 0,
    },
  }
}
export function editedMockBody(response: MockResponse, body: string): MockResponse {
  if (response.binary) return { ...response, body: '', bodyBase64: body }
  // Text edits are encoded as UTF-8; untouched recordings retain their exact bytes and charset.
  const contentType = response.contentType.replace(/;\s*charset\s*=\s*(?:"[^"]*"|[^;]*)/ig, '') + '; charset=utf-8'
  return { ...response, body, bodyBase64: null, contentType }
}
export function mockUrl(host: string, port: number, path = '', query = '') {
  const hostname = host.includes(':') && !host.startsWith('[') ? `[${host}]` : host
  return `http://${hostname}:${port}${path}${query ? '?' + query.replace(/^\?/, '') : ''}`
}
