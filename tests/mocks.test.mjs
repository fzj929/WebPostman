import test from 'node:test'
import assert from 'node:assert/strict'
import http from 'node:http'
import { gzipSync } from 'node:zlib'
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { mkdtemp, rm } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import { databaseEnvironmentFor } from './database-environment.mjs'

const root = path.resolve(import.meta.dirname, '..')
const listen = async server => { server.listen(0, '127.0.0.1'); await once(server, 'listening'); return `http://127.0.0.1:${server.address().port}` }
const free = async () => { const s = http.createServer(); const url = await listen(s); await new Promise(r => s.close(r)); return url }
const auth = { type: 'none', username: '', password: '', token: '', key: '', value: '', in: 'header' }
const realRequest = url => ({ id: 'record-source', name: '录制示例', method: 'GET', url, params: [], headers: [], cookies: [], bodyType: 'none', body: '', form: [], auth, folderId: '' })
const responseInput = result => ({ status: result.status, contentType: Object.entries(result.headers).find(([k]) => k.toLowerCase() === 'content-type')?.[1] || result.contentType, headers: Object.entries(result.headers).map(([key, value], i) => ({ id: '' + i, key, value, enabled: true })), body: result.bodyBase64 ? '' : result.body, bodyBase64: result.recordedBodyBase64, binary: result.bodyBase64 != null, delayMs: 0 })

test('record, edit and replay real responses on independently managed mock ports', async t => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'webpostman-mocks-'))
  const base = await free(), mockBase = await free(), port = Number(new URL(mockBase).port)
  let app, logs = '', upstreamCalls = 0, server, recorded, original, ruleId
  const raw = Buffer.from('{"source":"recorded","unicode":"中文"}')
  const upstream = http.createServer((req, res) => {
    upstreamCalls++
    if (req.url === '/binary') { res.setHeader('Content-Type', 'application/octet-stream'); res.end(Buffer.from([0, 255, 128, 1])); return }
    if (req.url === '/latin') { res.setHeader('Content-Type', 'text/plain; charset=iso-8859-1'); res.end(Buffer.from([99, 97, 102, 233])); return }
    if (req.url === '/large') { res.setHeader('Content-Type', 'text/plain'); res.end('x'.repeat(2 * 1024 * 1024 + 1)); return }
    res.writeHead(201, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Encoding': 'gzip', 'X-Recorded': 'kept', 'Set-Cookie': 'session=PRIVATE; HttpOnly' }); res.end(gzipSync(raw))
  })
  const upstreamUrl = await listen(upstream)
  const client = () => ({ cookie: '', async call(route, method = 'GET', body) {
    const response = await fetch(base + route, { method, headers: { 'Content-Type': 'application/json', Cookie: this.cookie }, body: body === undefined ? undefined : JSON.stringify(body) })
    for (const cookie of response.headers.getSetCookie()) if (cookie.startsWith('webpostman-session=') && !cookie.includes('1970')) this.cookie = cookie.split(';')[0]
    const text = await response.text(); return { status: response.status, data: text ? JSON.parse(text) : null }
  } })
  const a = client(), b = client(), guest = client()
  const start = async () => {
    app = spawn('dotnet', [path.join(root, 'WebPostman.Api/bin/Release/net8.0/WebPostman.Api.dll')], { cwd: path.join(root, 'WebPostman.Api'), env: { ...process.env, ...databaseEnvironmentFor('mocks'), WebPostman__ListenUrl: base, WebPostman__DataDirectory: directory }, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })
    logs = ''; app.stdout.on('data', x => logs += x); app.stderr.on('data', x => logs += x)
    for (let i = 0; i < 150; i++) { try { if ((await guest.call('/api/auth/me')).status === 200) return } catch {} if (app.exitCode !== null) break; await new Promise(r => setTimeout(r, 100)) }
    assert.fail(logs)
  }
  const stop = async () => { if (app && app.exitCode === null) { app.kill(); await once(app, 'exit') } }
  const execute = async suffix => {
    const result = await a.call('/api/execute', 'POST', { request: realRequest(upstreamUrl + suffix), effectiveAuth: auth, variables: {} })
    assert.equal(result.status, 200, logs); return result.data
  }
  const createRule = (name, path, response = original, extra = {}) => a.call(`/api/mocks/${server.id}/rules`, 'POST', { name, method: 'GET', path, query: '', enabled: true, response, sourceUrl: upstreamUrl + '/real?token=PRIVATE', revision: 0, ...extra })
  try {
    await start()
    await t.test('only authenticated owners can manage their mock servers', async () => {
      assert.equal((await guest.call('/api/mocks')).status, 401)
      for (const [c, email] of [[a, 'mock-a@example.test'], [b, 'mock-b@example.test']]) {
        const registered = await c.call('/api/auth/register', 'POST', { email, password: 'Mock-Password-123', confirmPassword: 'Mock-Password-123' }); assert.equal(registered.status, 200, logs)
      }
      assert.equal((await a.call('/api/mocks', 'POST', { name: 'Bad', port: 80, corsOrigins: ['*'] })).status, 400)
      server = (await a.call('/api/mocks', 'POST', { name: '前端联调', port, corsOrigins: ['*'] })).data
      assert.ok(server.id, logs); assert.equal(server.running, false)
      assert.deepEqual((await b.call('/api/mocks')).data, [])
      for (const [method, suffix] of [['GET', ''], ['POST', '/start'], ['POST', '/stop'], ['DELETE', ''], ['GET', '/logs']]) assert.equal((await b.call('/api/mocks/' + server.id + suffix, method)).status, 404)
      assert.equal((await a.call(`/api/mocks/${server.id}/start`, 'POST')).status, 400)
    })
    await t.test('captures decoded bytes, filters transport/cookie headers and starts a real HTTP listener', async () => {
      recorded = await execute('/real'); original = responseInput(recorded)
      assert.deepEqual(Buffer.from(recorded.recordedBodyBase64, 'base64'), raw)
      const saved = await createRule('录制回放', '/api/demo')
      assert.equal(saved.status, 200, logs); server = saved.data; ruleId = server.rules[0].id
      assert.ok(!server.rules[0].response.headers.some(x => /^(set-cookie|content-length|content-encoding|connection)$/i.test(x.key)))
      assert.ok(!server.rules[0].sourceUrl.includes('PRIVATE'))
      assert.equal((await a.call(`/api/mocks/${server.id}/start`, 'POST')).data.running, true, logs)
      const before = upstreamCalls, result = await fetch(mockBase + '/api/demo?unused=1')
      assert.equal(result.status, 201); assert.equal(result.headers.get('X-Recorded'), 'kept'); assert.equal(result.headers.get('Set-Cookie'), null)
      assert.equal(result.headers.get('Content-Encoding'), null); assert.deepEqual(Buffer.from(await result.arrayBuffer()), raw)
      assert.equal(upstreamCalls, before)
      assert.equal((await fetch(mockBase + '/unknown')).status, 404)
      assert.equal((await fetch(mockBase + '/api/mocks')).status, 404)
    })
    await t.test('editing status, headers, body and delay changes the next replay, with immutable original and stale-save protection', async () => {
      const rule = server.rules.find(x => x.id === ruleId)
      const changed = { ...rule, response: { ...rule.response, status: 503, contentType: 'application/json; charset=utf-8', body: '{"error":"模拟异常"}', bodyBase64: null, delayMs: 80, headers: [{ id: 'custom', key: 'X-Mock', value: 'edited', enabled: true }] } }
      const saved = await a.call(`/api/mocks/${server.id}/rules/${ruleId}`, 'PUT', changed)
      assert.equal(saved.status, 200, logs); server = saved.data
      const started = Date.now(), result = await fetch(mockBase + '/api/demo')
      assert.equal(result.status, 503); assert.equal(result.headers.get('X-Mock'), 'edited'); assert.deepEqual(await result.json(), { error: '模拟异常' }); assert.ok(Date.now() - started >= 60)
      assert.equal(server.rules.find(x => x.id === ruleId).originalResponse.body, recorded.body)
      assert.equal((await a.call(`/api/mocks/${server.id}/rules/${ruleId}`, 'PUT', changed)).status, 409)
      const reset = server.rules.find(x => x.id === ruleId)
      server = (await a.call(`/api/mocks/${server.id}/rules/${ruleId}`, 'PUT', { ...reset, response: reset.originalResponse })).data
      assert.deepEqual(Buffer.from(await (await fetch(mockBase + '/api/demo')).arrayBuffer()), raw)
    })
    await t.test('port conflicts and port changes while running produce actionable 409 errors', async () => {
      assert.equal((await a.call(`/api/mocks/${server.id}`, 'PUT', { name: server.name, port: port + 1, corsOrigins: ['*'], revision: server.revision })).status, 409)
      const other = (await b.call('/api/mocks', 'POST', { name: 'Other', port, corsOrigins: [] })).data
      assert.equal((await b.call(`/api/mocks/${other.id}/rules`, 'POST', { name: 'Other', method: 'GET', path: '/', query: '', enabled: true, response: original })).status, 200)
      assert.equal((await b.call(`/api/mocks/${other.id}/start`, 'POST')).status, 409)
      await b.call('/api/mocks/' + other.id, 'DELETE')
      const occupied = (await a.call('/api/mocks', 'POST', { name: 'Occupied', port: Number(new URL(upstreamUrl).port), corsOrigins: [] })).data
      await a.call(`/api/mocks/${occupied.id}/rules`, 'POST', { name: 'Occupied', method: 'GET', path: '/', query: '', enabled: true, response: original })
      assert.equal((await a.call(`/api/mocks/${occupied.id}/start`, 'POST')).status, 409)
      await a.call('/api/mocks/' + occupied.id, 'DELETE')
    })
    await t.test('query-specific routes take priority and browser preflights work without a session', async () => {
      const specific = await createRule('查询条件', '/api/demo', { ...original, status: 202, body: 'specific', bodyBase64: null }, { query: 'b=2&a=1' })
      assert.equal(specific.status, 200, logs)
      assert.equal((await fetch(mockBase + '/api/demo?a=1&b=2')).status, 202)
      assert.equal(await (await fetch(mockBase + '/api/demo?a=1&b=2')).text(), 'specific')
      assert.equal((await fetch(mockBase + '/api/demo?a=9')).status, 201)
      const cors = await fetch(mockBase + '/api/demo', { method: 'OPTIONS', headers: { Origin: 'https://frontend.example.test', 'Access-Control-Request-Method': 'POST', 'Access-Control-Request-Headers': 'Content-Type,X-Example' } })
      assert.equal(cors.status, 204); assert.equal(cors.headers.get('Access-Control-Allow-Origin'), '*'); assert.match(cors.headers.get('Access-Control-Allow-Headers'), /X-Example/i)
      assert.equal(cors.headers.get('Access-Control-Allow-Credentials'), null)
      server = (await a.call('/api/mocks/' + server.id)).data
      server = (await a.call(`/api/mocks/${server.id}`, 'PUT', { name: server.name, port, corsOrigins: ['https://allowed.example.test'], revision: server.revision })).data
      assert.equal((await fetch(mockBase + '/api/demo', { headers: { Origin: 'https://allowed.example.test' } })).headers.get('Access-Control-Allow-Origin'), 'https://allowed.example.test')
      assert.equal((await fetch(mockBase + '/api/demo', { headers: { Origin: 'https://other.example.test' } })).headers.get('Access-Control-Allow-Origin'), null)
    })
    await t.test('binary and non-UTF8 recordings replay byte-for-byte; HEAD and bodyless statuses obey HTTP', async () => {
      for (const suffix of ['/binary', '/latin']) {
        const real = await execute(suffix), saved = await createRule(suffix, suffix, responseInput(real)); assert.equal(saved.status, 200, logs)
        const result = await fetch(mockBase + suffix); assert.deepEqual(Buffer.from(await result.arrayBuffer()), Buffer.from(real.recordedBodyBase64, 'base64'))
      }
      const head = await fetch(mockBase + '/api/demo', { method: 'HEAD' }); assert.equal(head.status, 201); assert.equal(await head.text(), ''); assert.equal(Number(head.headers.get('Content-Length')), raw.length)
      assert.equal((await createRule('无内容', '/empty', { ...original, status: 204 })).status, 200)
      const empty = await fetch(mockBase + '/empty'); assert.equal(empty.status, 204); assert.equal(await empty.text(), '')
    })
    await t.test('invalid and duplicate rules cannot break the live service', async () => {
      assert.equal((await createRule('重复', '/api/demo')).status, 409)
      assert.equal((await createRule('截断', '/truncated', original, { truncated: true })).status, 400)
      const large = await execute('/large'); assert.equal(large.truncated, true)
      assert.equal((await createRule('坏头', '/bad-header', { ...original, headers: [{ id: 'bad', key: 'X-Bad', value: 'a\r\nb', enabled: true }] })).status, 400)
      assert.equal((await createRule('坏编码', '/bad-base64', { ...original, bodyBase64: 'bad!!!' })).status, 400)
      assert.equal((await createRule('坏延迟', '/bad-delay', { ...original, delayMs: 999999 })).status, 400)
      assert.equal((await createRule('坏路径', 'not-a-path')).status, 400)
      assert.equal((await fetch(mockBase + '/api/demo')).status, 201)
    })
    await t.test('disabled rules return 404; logs are bounded metadata without query secrets', async () => {
      server = (await a.call('/api/mocks/' + server.id)).data
      const rule = server.rules.find(x => x.id === ruleId)
      assert.equal((await a.call(`/api/mocks/${server.id}/rules/${ruleId}`, 'PUT', { ...rule, enabled: false })).status, 200)
      assert.equal((await fetch(mockBase + '/api/demo')).status, 404)
      for (let i = 0; i < 55; i++) await (await fetch(mockBase + '/missing?secret=PRIVATE')).arrayBuffer()
      const calls = (await a.call(`/api/mocks/${server.id}/logs`)).data
      assert.equal(calls.length, 50); assert.ok(!JSON.stringify(calls).includes('PRIVATE')); assert.equal(calls[0].status, 404)
    })
    await t.test('restart retains snapshots but leaves ports stopped; stop/delete release their listeners', async () => {
      await stop(); await start()
      server = (await a.call('/api/mocks/' + server.id)).data
      assert.equal(server.running, false); assert.ok(server.rules.length >= 5)
      assert.deepEqual((await a.call(`/api/mocks/${server.id}/logs`)).data, [])
      assert.equal((await a.call(`/api/mocks/${server.id}/start`, 'POST')).status, 200, logs)
      assert.equal((await fetch(mockBase + '/binary')).status, 200)
      assert.equal((await a.call(`/api/mocks/${server.id}/stop`, 'POST')).data.running, false)
      await assert.rejects(() => fetch(mockBase + '/binary'))
      assert.equal((await a.call(`/api/mocks/${server.id}/start`, 'POST')).status, 200)
      assert.equal((await a.call('/api/mocks/' + server.id, 'DELETE')).status, 204)
      await assert.rejects(() => fetch(mockBase + '/binary'))
      assert.equal((await a.call('/api/mocks/' + server.id)).status, 404)
    })
  } finally {
    await stop(); upstream.closeAllConnections(); await new Promise(resolve => upstream.close(resolve))
    if (!directory.startsWith(path.join(os.tmpdir(), 'webpostman-mocks-'))) throw new Error('Unexpected cleanup path')
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 })
  }
})
