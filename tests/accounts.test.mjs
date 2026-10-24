import test from 'node:test'
import assert from 'node:assert/strict'
import http from 'node:http'
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { mkdtemp, rm, stat } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'

const root = path.resolve(import.meta.dirname, '..')
const auth = { type: 'none', username: '', password: '', token: '', key: '', value: '', in: 'header' }
const request = { id: 'private-r', name: 'Private request', method: 'POST', url: 'https://example.test?access_token=SENSITIVE', params: [], headers: [{ id: 'header', key: 'X-Api-Key', value: 'SENSITIVE', enabled: true }], cookies: [{ id: 'cookie', key: 'session', value: 'SENSITIVE', enabled: true }], bodyType: 'json', body: '{"password":"SENSITIVE","sample":"kept"}', form: [], auth: { ...auth, type: 'bearer', token: 'SENSITIVE' }, folderId: '', files: [{ key: 'file', fileName: 'a.txt', contentType: 'text/plain', base64: Buffer.from('private-file').toString('base64') }], allowInsecureSsl: false }
const listen = async server => { server.listen(0, '127.0.0.1'); await once(server, 'listening'); return `http://127.0.0.1:${server.address().port}` }

test('accounts, anonymous privacy and public request/collection sharing', async t => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'webpostman-accounts-'))
  const reserve = http.createServer(); const base = await listen(reserve); await new Promise(r => reserve.close(r))
  const app = spawn('dotnet', [path.join(root, 'WebPostman.Api/bin/Release/net8.0/WebPostman.Api.dll')], { cwd: path.join(root, 'WebPostman.Api'), env: { ...process.env, WebPostman__ListenUrl: base, WebPostman__DataDirectory: directory }, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })
  let logs = ''; app.stdout.on('data', d => logs += d); app.stderr.on('data', d => logs += d)
  const client = () => ({ cookie: '', async call(route, method = 'GET', body, extraHeaders = {}) {
    const result = await fetch(base + route, { method, headers: { 'Content-Type': 'application/json', ...(this.cookie ? { Cookie: this.cookie } : {}), ...extraHeaders }, body: body === undefined ? undefined : JSON.stringify(body) })
    const cookies = result.headers.getSetCookie()
    for (const cookie of cookies) if (cookie.startsWith('webpostman-session=')) this.cookie = cookie.split(';')[0]
    const text = await result.text(); return { status: result.status, data: text ? JSON.parse(text) : null, cookies }
  } })
  const guest = client(), a = client(), b = client()
  const password = 'Synthetic-Password-123'
  let userA, userB, publishedRequest, publishedCollection
  const patch = (revision, collections) => ({ revision, collections, deletedCollections: [], environments: [], deletedEnvironments: [] })
  let proxyCalls = 0
  const upstream = http.createServer((req, res) => { proxyCalls++; const finish = () => { if (!res.destroyed) { res.setHeader('Content-Type', 'application/json'); res.end('{"ok":true}') } }; if (req.url === '/slow') setTimeout(finish, 800); else finish() })
  const upstreamUrl = await listen(upstream)
  try {
    let ready = false
    for (let i = 0; i < 100; i++) { try { if ((await guest.call('/api/auth/me')).status === 200) { ready = true; break } } catch {} await new Promise(r => setTimeout(r, 100)) }
    assert.ok(ready, logs)
    await t.test('registration validates confirmation, normalizes email and sets private session cookie', async () => {
      assert.equal((await guest.call('/api/auth/register', 'POST', { email: 'a@example.test', password, confirmPassword: 'wrong' })).status, 400)
      const registered = await a.call('/api/auth/register', 'POST', { email: ' A@Example.Test ', password, confirmPassword: password })
      assert.equal(registered.status, 200); userA = registered.data.user
      assert.equal(userA.email, 'a@example.test')
      const cookie = registered.cookies.find(x => x.startsWith('webpostman-session=') && !x.includes('1970'))
      assert.match(cookie, /HttpOnly/i); assert.match(cookie, /SameSite=Strict/i); assert.match(cookie, /path=\/api/i)
      assert.equal((await guest.call('/api/auth/register', 'POST', { email: 'A@example.test', password, confirmPassword: password })).status, 409)
      userB = (await b.call('/api/auth/register', 'POST', { email: 'b@example.test', password, confirmPassword: password })).data.user
      assert.notEqual(userA.id, userB.id)
    })
    await t.test('guests cannot access server workspaces; guest execution never persists history', async () => {
      assert.equal((await guest.call('/api/workspace')).status, 401)
      assert.equal((await guest.call('/api/workspace', 'PATCH', patch(0, []))).status, 401)
      assert.equal((await guest.call('/api/history')).status, 401)
      const result = await guest.call('/api/execute', 'POST', { request: { ...request, files: null, url: upstreamUrl }, effectiveAuth: auth, variables: {} }, { 'X-WebPostman-User': 'guest' })
      assert.equal(result.status, 200); assert.ok(result.data.history.id)
      assert.equal((await a.call('/api/history')).data.total, 0)
      await assert.rejects(stat(path.join(directory, 'workspace.db')), { code: 'ENOENT' })
    })
    await t.test('account workspaces and source IDs are isolated, even with forged owner fields', async () => {
      const collection = { id: 'private-c', name: 'A private collection', auth: { ...auth, type: 'bearer', token: 'SENSITIVE' }, folders: [], requests: [{ ...request, auth: { ...auth, type: 'inherit' } }] }
      assert.equal((await a.call('/api/workspace', 'PATCH', patch(0, [collection]))).status, 200)
      assert.equal((await b.call('/api/workspace')).data.collections.length, 0)
      assert.equal((await b.call('/api/shares/preview', 'POST', { kind: 'collection', collectionId: 'private-c', title: 'Not mine' })).status, 404)
      assert.equal((await b.call('/api/workspace', 'PATCH', { ...patch(0, [{ id: 'b-c', name: 'B only', auth, folders: [], requests: [] }]), userId: userA.id })).status, 200)
      assert.equal((await a.call('/api/workspace')).data.collections[0].name, 'A private collection')
      assert.equal((await b.call('/api/workspace')).data.collections[0].name, 'B only')
    })
    await t.test('cross-tab account switches and cross-origin changes are rejected', async () => {
      assert.equal((await b.call('/api/workspace', 'GET', undefined, { 'X-WebPostman-User': userA.id })).data.code, 'account_changed')
      const count = proxyCalls
      assert.equal((await a.call('/api/execute', 'POST', { request: { ...request, url: upstreamUrl }, effectiveAuth: auth, variables: {} }, { 'X-WebPostman-User': 'guest' })).status, 409)
      assert.equal(proxyCalls, count)
      assert.equal((await guest.call('/api/auth/login', 'POST', { email: 'a@example.test', password }, { Origin: 'https://evil.example' })).status, 403)
    })
    await t.test('request sharing publishes an immutable scrubbed snapshot visible to everyone', async () => {
      const input = { kind: 'request', collectionId: 'private-c', requestId: 'private-r', title: 'Public request', description: 'Reusable sample' }
      assert.equal((await guest.call('/api/shares', 'POST', input)).status, 401)
      const preview = (await a.call('/api/shares/preview', 'POST', input)).data
      assert.ok(!JSON.stringify(preview.collection).includes('SENSITIVE'))
      assert.equal(preview.collection.requests[0].files, null)
      assert.equal(preview.collection.requests[0].auth.type, 'bearer'); assert.equal(preview.collection.requests[0].auth.token, '{{token}}')
      assert.equal(JSON.parse(preview.collection.requests[0].body).sample, 'kept')
      publishedRequest = (await a.call('/api/shares', 'POST', { ...input, revision: preview.revision, ownerId: userB.id })).data
      assert.equal(publishedRequest.ownerId, userA.id)
      const publicCopy = (await guest.call(`/api/shares/${publishedRequest.id}`)).data
      assert.equal(publicCopy.title, 'Public request'); assert.ok(!JSON.stringify(publicCopy).includes(userA.email))
      assert.equal((await guest.call('/api/shares?kind=request&query=Reusable')).data.total, 1)
      const w = (await a.call('/api/workspace')).data
      w.collections[0].requests[0].name = 'Changed original'
      assert.equal((await a.call('/api/workspace', 'PUT', w)).status, 200)
      assert.equal((await guest.call(`/api/shares/${publishedRequest.id}`)).data.collection.requests[0].name, 'Public request')
      assert.equal((await a.call('/api/shares', 'POST', { ...input, revision: preview.revision })).status, 409)
    })
    await t.test('collection shares can be copied by another account and revoked only by the author', async () => {
      const input = { kind: 'collection', collectionId: 'private-c', title: 'Public collection' }
      const preview = (await a.call('/api/shares/preview', 'POST', input)).data
      assert.equal(preview.collection.auth.token, '{{token}}'); assert.equal(preview.collection.requests[0].auth.type, 'inherit')
      publishedCollection = (await a.call('/api/shares', 'POST', { ...input, revision: preview.revision })).data
      assert.equal((await guest.call('/api/shares?kind=collection')).data.total, 1)
      const own = (await b.call('/api/workspace')).data
      const copy = structuredClone(publishedCollection.collection); copy.id = 'copied-collection'; copy.requests[0].id = 'copied-request'
      assert.equal((await b.call('/api/workspace', 'PATCH', patch(own.revision, [copy]))).status, 200)
      assert.equal((await b.call(`/api/shares/${publishedCollection.id}`, 'DELETE')).status, 404)
      assert.equal((await a.call(`/api/shares/${publishedCollection.id}`, 'DELETE')).status, 204)
      assert.equal((await guest.call(`/api/shares/${publishedCollection.id}`)).status, 404)
      assert.ok((await b.call('/api/workspace')).data.collections.some(x => x.id === 'copied-collection'))
      assert.equal((await a.call('/api/shares?mine=true')).data.total, 1)
    })
    await t.test('execution concurrency is bounded for anonymous users', async () => {
      const before = proxyCalls
      const body = { request: { ...request, files: null, url: upstreamUrl + '/slow' }, effectiveAuth: auth, variables: {} }
      const pending = Array.from({ length: 4 }, () => guest.call('/api/execute', 'POST', body))
      for (let n = 0; n < 50 && proxyCalls < before + 4; n++) await new Promise(resolve => setTimeout(resolve, 10))
      assert.equal((await guest.call('/api/execute', 'POST', body)).status, 429)
      assert.ok((await Promise.all(pending)).every(x => x.status === 200))
      assert.equal(proxyCalls, before + 4)
    })
    await t.test('logout revokes the old token and login restores account data', async () => {
      const oldCookie = a.cookie
      assert.equal((await a.call('/api/auth/logout', 'POST')).status, 200)
      const stolen = client(); stolen.cookie = oldCookie
      assert.equal((await stolen.call('/api/workspace')).status, 401)
      assert.equal((await a.call('/api/auth/me')).data.user, null)
      assert.equal((await a.call('/api/auth/login', 'POST', { email: 'a@example.test', password: 'wrong-password' })).status, 401)
      assert.equal((await a.call('/api/auth/login', 'POST', { email: 'A@EXAMPLE.TEST', password })).status, 200)
      assert.equal((await a.call('/api/workspace')).data.collections[0].id, 'private-c')
    })
    await t.test('authentication attempts are rate limited', async () => {
      let limited = false
      for (let i = 0; i < 32; i++) if ((await guest.call('/api/auth/login', 'POST', { email: 'bad', password: 'too-short' })).status === 429) { limited = true; break }
      assert.equal(limited, true)
    })
  } finally {
    app.kill(); await once(app, 'exit').catch(() => {})
    upstream.closeAllConnections(); upstream.close()
    if (!directory.startsWith(path.join(os.tmpdir(), 'webpostman-accounts-'))) throw new Error('Unexpected cleanup path')
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 })
  }
})
