import test from 'node:test'
import assert from 'node:assert/strict'
import http from 'node:http'
import { spawn, spawnSync } from 'node:child_process'
import { once } from 'node:events'
import { mkdtemp, rm } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import { databaseEnvironmentFor } from './database-environment.mjs'

const root = path.resolve(import.meta.dirname, '..')
const python = process.platform === 'win32' ? 'python' : 'python3'
const auth = { type: 'none', username: '', password: '', token: '', key: '', value: '', in: 'header' }
const collection = id => ({ id, name: id, auth, folders: [], requests: [] })

test('EF schema, legacy account import, atomic edits and restart persistence', async t => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'webpostman-storage-'))
  const userId = '1234567890abcdef1234567890abcdef', email = 'legacy-storage@example.test', password = 'Legacy-Password-123'
  const token = 'AB'.repeat(32), shareId = 'abcdef1234567890abcdef1234567890'
  const oldShare = { id: shareId, ownerId: userId, author: '用户 123456', kind: 'collection', title: '旧版分享', description: 'migration', createdAt: '2025-01-01T00:00:00Z', collection: collection('old-shared') }
  const legacy = { collections: [collection('legacy-first'), collection('legacy-second')], environments: [{ id: 'old-env', name: '旧环境', variables: [] }], history: [{ id: 'old-history', at: '2025-01-01T00:00:00Z', method: 'GET', url: 'https://example.test', status: 200, durationMs: 1 }], revision: 7 }
  const seed = spawnSync(python, ['-c', `
import sqlite3,sys,json,pathlib,hashlib,base64,struct,time
d=pathlib.Path(sys.argv[1]); uid,email,password,token=sys.argv[2:6]
salt=b'legacy-test-salt'; digest=hashlib.pbkdf2_hmac('sha512',password.encode(),salt,600000,32)
h=base64.b64encode(b'\\x01'+struct.pack('>III',2,600000,len(salt))+salt+digest).decode()
c=sqlite3.connect(d/'accounts.db')
c.executescript('CREATE TABLE users(id TEXT PRIMARY KEY,email TEXT UNIQUE,password_hash TEXT); CREATE TABLE sessions(token_hash TEXT PRIMARY KEY,user_id TEXT,expires INTEGER); CREATE TABLE shares(id TEXT PRIMARY KEY,owner_id TEXT,kind TEXT,title TEXT,description TEXT,created_at TEXT,json TEXT);')
c.execute('INSERT INTO users VALUES(?,?,?)',(uid,email,h)); c.execute('INSERT INTO sessions VALUES(?,?,?)',(hashlib.sha256(token.encode()).hexdigest().upper(),uid,int(time.time())+3600))
s=json.loads(sys.argv[6]); c.execute('INSERT INTO shares VALUES(?,?,?,?,?,?,?)',(s['id'],uid,s['kind'],s['title'],s['description'],s['createdAt'],sys.argv[6])); c.commit(); c.close()
u=d/'users'/uid; u.mkdir(parents=True); w=json.loads(sys.argv[7]); c=sqlite3.connect(u/'workspace.db')
c.executescript('CREATE TABLE workspace_state(id INTEGER PRIMARY KEY,revision INTEGER); CREATE TABLE collections(id TEXT PRIMARY KEY,json TEXT,position INTEGER); CREATE TABLE environments(id TEXT PRIMARY KEY,json TEXT,position INTEGER); CREATE TABLE history(id TEXT PRIMARY KEY,at TEXT,json TEXT);')
c.execute('INSERT INTO workspace_state VALUES(1,?)',(w['revision'],))
for table in ['collections','environments']:
 for i,x in enumerate(w[table]): c.execute('INSERT INTO '+table+' VALUES(?,?,?)',(x['id'],json.dumps(x),i))
for x in w['history']: c.execute('INSERT INTO history VALUES(?,?,?)',(x['id'],x['at'],json.dumps(x)))
c.commit(); c.close()
# An unowned workspace must never be assigned to the registered account.
c=sqlite3.connect(d/'workspace.db'); c.execute('CREATE TABLE workspace(id INTEGER PRIMARY KEY,json TEXT)'); c.execute('INSERT INTO workspace VALUES(1,?)',('unowned sentinel',)); c.commit(); c.close()
`, directory, userId, email, password, token, JSON.stringify(oldShare), JSON.stringify(legacy)], { encoding: 'utf8' })
  assert.equal(seed.status, 0, seed.stderr)
  const reserve = http.createServer(); reserve.listen(0, '127.0.0.1'); await once(reserve, 'listening')
  const base = `http://127.0.0.1:${reserve.address().port}`; await new Promise(resolve => reserve.close(resolve))
  let app, logs = '', cookie = `webpostman-session=${token}`
  const call = async (route, method = 'GET', body) => {
    const response = await fetch(base + route, { method, headers: { 'Content-Type': 'application/json', Cookie: cookie }, body: body === undefined ? undefined : JSON.stringify(body) })
    const text = await response.text()
    return { status: response.status, data: text ? JSON.parse(text) : null, response }
  }
  const start = async () => {
    app = spawn('dotnet', [path.join(root, 'WebPostman.Api/bin/Release/net8.0/WebPostman.Api.dll')], { cwd: path.join(root, 'WebPostman.Api'), env: { ...process.env, ...databaseEnvironmentFor('storage'), WebPostman__ListenUrl: base, WebPostman__DataDirectory: directory }, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })
    logs = ''; app.stdout.on('data', x => logs += x); app.stderr.on('data', x => logs += x)
    for (let i = 0; i < 150; i++) { try { if ((await call('/api/auth/me')).status === 200) return } catch {} if (app.exitCode !== null) break; await new Promise(resolve => setTimeout(resolve, 100)) }
    assert.fail(logs)
  }
  const stop = async () => { if (app && app.exitCode === null) { app.kill(); await once(app, 'exit') } }
  const patch = (revision, collections = [], extra = {}) => ({ revision, collections, deletedCollections: [], environments: [], deletedEnvironments: [], ...extra })
  try {
    await start()
    await t.test('imports legacy password, live session, shares, order, revision and history', async () => {
      assert.equal((await call('/api/auth/me')).data.user.id, userId, logs)
      const w = (await call('/api/workspace')).data
      assert.equal(w.revision, 7); assert.deepEqual(w.collections.map(x => x.id), ['legacy-first', 'legacy-second'])
      assert.equal(w.environments[0].id, 'old-env'); assert.deepEqual(w.history, [])
      assert.equal((await call('/api/history')).data.items[0].id, 'old-history')
      assert.equal((await call('/api/shares/' + shareId)).data.title, '旧版分享')
      const login = await call('/api/auth/login', 'POST', { email, password })
      assert.equal(login.status, 200, logs)
      cookie = login.response.headers.getSetCookie().find(x => x.startsWith('webpostman-session=') && !x.includes('1970')).split(';')[0]
    })
    await t.test('case-sensitive IDs and delete/upsert ordering work on both providers', async () => {
      assert.equal((await call('/api/workspace', 'PATCH', patch(7, [collection('Case'), collection('case'), collection('case ')], { deletedCollections: ['legacy-second'], deletedEnvironments: ['old-env'] }))).status, 200, logs)
      const w = (await call('/api/workspace')).data
      assert.deepEqual(w.collections.map(x => x.id), ['legacy-first', 'Case', 'case', 'case ']); assert.deepEqual(w.environments, [])
    })
    await t.test('concurrent saves have one winner and one 409, with no lost changes', async () => {
      const results = await Promise.all(['winner-a', 'winner-b'].map(id => call('/api/workspace', 'PATCH', patch(8, [collection(id)]))))
      assert.deepEqual(results.map(x => x.status).sort(), [200, 409], logs)
      const w = (await call('/api/workspace')).data
      assert.equal(w.revision, 9); assert.equal(w.collections.filter(x => x.id.startsWith('winner-')).length, 1)
    })
    await t.test('entity limit failure rolls back the revision and all inserted data', async () => {
      const result = await call('/api/workspace', 'PATCH', patch(9, Array.from({ length: 500 }, (_, i) => collection('overflow-' + i))))
      assert.equal(result.status, 400, logs)
      const w = (await call('/api/workspace')).data; assert.equal(w.revision, 9); assert.equal(w.collections.length, 5)
      assert.equal((await call('/api/shares/' + shareId, 'DELETE')).status, 204)
    })
    await t.test('restart preserves EF changes and never reimports withdrawn legacy data', async () => {
      await stop(); await start()
      assert.equal((await call('/api/auth/me')).data.user.id, userId)
      const w = (await call('/api/workspace')).data; assert.equal(w.revision, 9); assert.equal(w.collections.length, 5)
      assert.equal((await call('/api/shares/' + shareId)).status, 404)
      assert.equal((await call('/api/history')).data.total, 1)
      const check = spawnSync(python, ['-c', `import sqlite3,sys,pathlib; d=pathlib.Path(sys.argv[1]); c=sqlite3.connect(d/'workspace.db'); assert c.execute('SELECT json FROM workspace').fetchone()[0]=='unowned sentinel'; c.close(); c=sqlite3.connect(d/'accounts.db'); assert c.execute('SELECT COUNT(*) FROM shares').fetchone()[0]==1; c.close(); c=sqlite3.connect(d/'users'/sys.argv[2]/'workspace.db'); assert c.execute('SELECT revision FROM workspace_state').fetchone()[0]==7`, directory, userId], { encoding: 'utf8' })
      assert.equal(check.status, 0, check.stderr)
    })
  } finally {
    await stop()
    if (!directory.startsWith(path.join(os.tmpdir(), 'webpostman-storage-'))) throw new Error('Unexpected cleanup path')
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 })
  }
})
