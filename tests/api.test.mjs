import test from 'node:test'
import assert from 'node:assert/strict'
import http from 'node:http'
import { spawn, spawnSync } from 'node:child_process'
import { once } from 'node:events'
import { mkdtemp, rm } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'

const root = path.resolve(import.meta.dirname, '..')
const auth = { type: 'none', username: '', password: '', token: '', key: '', value: '', in: 'header' }
const request = { id: 'request', name: 'Test', method: 'GET', url: '', params: [], headers: [], cookies: [], bodyType: 'none', body: '', form: [], auth, folderId: '', allowInsecureSsl: false, timeoutSeconds: 60, followRedirects: true }
const listen = async server => { server.listen(0, '127.0.0.1'); await once(server, 'listening'); return `http://127.0.0.1:${server.address().port}` }
const freePort = async () => { const s = http.createServer(); const url = await listen(s); await new Promise(resolve => s.close(resolve)); return url }

test('API regression: migration, conflicts, validation, HTTP execution and history', async t => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'webpostman-test-'))
  const legacy = { collections: [{ id: 'old', name: 'Legacy', auth, folders: [], requests: [] }], environments: [], history: [{ id: 'history-old', at: '2025-01-01T00:00:00+00:00', method: 'GET', url: 'https://example.test', status: 200, durationMs: 5 }] }
  const python = process.platform === 'win32' ? 'python' : 'python3'
  const seed = spawnSync(python, ['-c', 'import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute("CREATE TABLE workspace (id INTEGER PRIMARY KEY,json TEXT NOT NULL)"); c.execute("INSERT INTO workspace VALUES(1,?)",(sys.argv[2],)); c.commit()', path.join(directory, 'workspace.db'), JSON.stringify(legacy)], { encoding: 'utf8' })
  assert.equal(seed.status, 0, seed.stderr)
  let targetUrl
  const upstream = http.createServer(async (req, res) => {
    if (req.url === '/redirect') { res.writeHead(302, { Location: '/final' }); res.end(); return }
    if (req.url === '/cross') { res.writeHead(302, { Location: targetUrl }); res.end(); return }
    if (req.url === '/slow') { await new Promise(resolve => setTimeout(resolve, 2000)); if (req.destroyed || res.destroyed) return }
    if (req.url === '/binary') { res.setHeader('Content-Type', 'application/octet-stream'); res.setHeader('Content-Disposition', 'attachment; filename="sample.bin"'); res.end(Buffer.from([0, 255, 128, 1])); return }
    if (req.url === '/large') { res.setHeader('Content-Type', 'text/plain'); res.end('a'.repeat(2 * 1024 * 1024 + 100)); return }
    const chunks = []; for await (const chunk of req) chunks.push(chunk)
    res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify({ path: req.url, method: req.method, body: Buffer.concat(chunks).toString(), headers: req.headers }))
  })
  const cross = http.createServer((req, res) => { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(req.headers)) })
  targetUrl = await listen(cross); const upstreamUrl = await listen(upstream); const base = await freePort()
  const app = spawn('dotnet', [path.join(root, 'WebPostman.Api/bin/Release/net8.0/WebPostman.Api.dll')], { cwd: path.join(root, 'WebPostman.Api'), env: { ...process.env, WebPostman__DataDirectory: directory, WebPostman__ListenUrl: base }, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })
  let logs = ''; app.stdout.on('data', d => logs += d); app.stderr.on('data', d => logs += d)
  const call = async (route, method = 'GET', body, signal) => {
    const response = await fetch(base + route, { method, headers: { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body), signal })
    const raw = await response.text(); return { status: response.status, data: raw ? JSON.parse(raw) : null }
  }
  const execute = (overrides = {}, effectiveAuth = auth, variables = {}) => call('/api/execute', 'POST', { request: { ...request, url: upstreamUrl, ...overrides }, effectiveAuth, variables })
  try {
    let ready = false
    for (let n = 0; n < 100; n++) { try { if ((await call('/api/workspace')).status === 200) { ready = true; break } } catch {} await new Promise(r => setTimeout(r, 100)) }
    assert.ok(ready, logs)
    await t.test('legacy migration keeps entities and history, independent history pagination', async () => {
      const w = (await call('/api/workspace')).data
      assert.equal(w.collections[0].name, 'Legacy'); assert.deepEqual(w.history, [])
      const h = (await call('/api/history?offset=0&limit=1')).data
      assert.equal(h.total, 1); assert.equal(h.items[0].id, 'history-old')
    })
    await t.test('stale saves return 409 and cannot erase another page changes', async () => {
      const w = (await call('/api/workspace')).data
      const changes = { revision: w.revision, collections: [], deletedCollections: [], environments: [{ id: 'env', name: 'New', variables: [] }], deletedEnvironments: [] }
      assert.equal((await call('/api/workspace', 'PATCH', changes)).status, 200)
      assert.equal((await call('/api/workspace', 'PUT', w)).status, 409)
      assert.equal((await call('/api/workspace')).data.environments[0].name, 'New')
    })
    await t.test('invalid DTOs return 400 instead of 500', async () => {
      for (const body of [{}, { collections: [null], environments: [] }, { collections: [], environments: [{ id: 'e', name: 'bad', variables: null }] }]) assert.equal((await call('/api/workspace', 'PUT', body)).status, 400)
      assert.equal((await call('/api/execute', 'POST', { request: null, effectiveAuth: auth, variables: {} })).status, 400)
      assert.equal((await execute({ timeoutSeconds: 0 })).status, 400)
    })
    await t.test('redirect control, final trace and lowercase GET body semantics', async () => {
      const result = (await execute({ url: upstreamUrl + '/redirect' })).data
      assert.equal(new URL(result.trace.url).pathname, '/final'); assert.equal(new URL(result.trace.originalUrl).pathname, '/redirect')
      assert.equal((await execute({ url: upstreamUrl + '/redirect', followRedirects: false })).data.status, 302)
      assert.equal(JSON.parse((await execute({ method: 'get', bodyType: 'json', body: 'not sent' })).data.body).body, '')
    })
    await t.test('auth/variables applied, diagnostic secrets hidden and cross-origin credentials removed', async () => {
      const token = { ...auth, type: 'apiKey', key: 'access_token', value: '{{secret}}', in: 'query' }
      const result = (await execute({ method: 'POST', bodyType: 'json', body: '{"password":"{{secret}}"}', headers: [{ id: 'h', key: 'X-Token', value: '{{secret}}', enabled: true }] }, token, { secret: 'synthetic-secret' })).data
      assert.ok(JSON.parse(result.body).path.includes('synthetic-secret'))
      assert.ok(!JSON.stringify(result.trace).includes('synthetic-secret'))
      const crossResult = (await execute({ url: upstreamUrl + '/cross', headers: [{ id: 'h', key: 'X-Token', value: 'secret', enabled: true }] }, { ...auth, type: 'bearer', token: 'secret' })).data
      const headers = JSON.parse(crossResult.body); assert.equal(headers.authorization, undefined); assert.equal(headers['x-token'], undefined)
    })
    await t.test('binary download exact bytes and explicit truncation', async () => {
      const binary = (await execute({ url: upstreamUrl + '/binary' })).data
      assert.deepEqual(Buffer.from(binary.bodyBase64, 'base64'), Buffer.from([0, 255, 128, 1])); assert.equal(binary.fileName, 'sample.bin')
      assert.equal((await execute({ url: upstreamUrl + '/large' })).data.truncated, true)
    })
    await t.test('basic auth, form and multipart requests preserve encoded fields and files', async () => {
      const basic = { ...auth, type: 'basic', username: '{{user}}', password: 'p:a' }
      const form = (await execute({ method: 'POST', bodyType: 'form', form: [{ id: 'row', key: 'q', value: 'a b&c', enabled: true }] }, basic, { user: 'demo' })).data
      const echoed = JSON.parse(form.body)
      assert.equal(echoed.headers.authorization, 'Basic ' + Buffer.from('demo:p:a').toString('base64'))
      assert.equal(echoed.body, 'q=a+b%26c')
      const multi = (await execute({ method: 'POST', bodyType: 'multipart', form: [{ id: 'row', key: 'name', value: 'sample', enabled: true }], files: [{ key: 'document', fileName: 'sample.txt', contentType: 'text/plain', base64: Buffer.from('file contents').toString('base64') }] })).data
      const raw = JSON.parse(multi.body).body
      assert.ok(raw.includes('name=document')); assert.ok(raw.includes('sample.txt')); assert.ok(raw.includes('file contents'))
      const reveal = (await execute({ showSensitiveTrace: true }, { ...auth, type: 'bearer', token: 'revealed-only-on-request' })).data
      assert.equal(reveal.trace.requestHeaders.Authorization, 'Bearer revealed-only-on-request')
    })
    await t.test('timeouts and cancelled calls do not append successful history', async () => {
      const before = (await call('/api/history')).data.total
      assert.equal((await execute({ url: upstreamUrl + '/slow', timeoutSeconds: 1 })).status, 400)
      await assert.rejects(() => call('/api/execute', 'POST', { request: { ...request, url: upstreamUrl + '/slow' }, effectiveAuth: auth, variables: {} }, AbortSignal.timeout(100)))
      await new Promise(r => setTimeout(r, 150))
      assert.equal((await call('/api/history')).data.total, before)
      assert.equal((await call('/api/workspace')).status, 200)
    })
  } finally {
    app.kill(); await once(app, 'exit').catch(() => {})
    upstream.closeAllConnections(); cross.closeAllConnections(); upstream.close(); cross.close()
    if (!directory.startsWith(path.join(os.tmpdir(), 'webpostman-test-'))) throw new Error('Unexpected cleanup path')
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 })
  }
})
