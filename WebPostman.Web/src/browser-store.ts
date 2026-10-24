import type { History, Workspace } from './types'

const empty = (): Workspace => ({ collections: [], environments: [], history: [], revision: 0 })
async function database(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    if (typeof indexedDB === 'undefined') { reject(new Error('浏览器存储不可用，请使用普通浏览模式或导出备份')); return }
    const request = indexedDB.open('webpostman-browser', 1)
    request.onupgradeneeded = () => { if (!request.result.objectStoreNames.contains('data')) request.result.createObjectStore('data') }
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(new Error('无法打开浏览器存储，请检查浏览器隐私设置'))
  })
}
export async function readGuestWorkspace(): Promise<Workspace> {
  const db = await database()
  return new Promise((resolve, reject) => {
    const tx = db.transaction('data', 'readonly'); const get = tx.objectStore('data').get('workspace')
    get.onsuccess = () => resolve(get.result ?? empty())
    tx.onerror = () => reject(new Error('读取本机工作区失败')); tx.oncomplete = () => db.close()
  })
}
export async function saveGuestWorkspace(workspace: Workspace, expectedRevision: number): Promise<number> {
  const db = await database()
  return new Promise((resolve, reject) => {
    const tx = db.transaction('data', 'readwrite'); const store = tx.objectStore('data'); const get = store.get('workspace')
    let revision = 0; let conflict = false
    get.onsuccess = () => {
      if ((get.result?.revision ?? 0) !== expectedRevision) { conflict = true; tx.abort(); return }
      revision = expectedRevision + 1
      store.put({ collections: workspace.collections, environments: workspace.environments, history: [], revision }, 'workspace')
    }
    tx.oncomplete = () => { db.close(); resolve(revision) }
    tx.onabort = () => { db.close(); reject(new Error(conflict ? '本机工作区已被其他页面修改，请先导出备份再重新载入' : '浏览器保存失败，可能存储空间不足；请导出备份')) }
    tx.onerror = () => { /* onabort reports a single actionable error. */ }
  })
}
export async function readGuestHistory(): Promise<History[]> {
  const db = await database()
  return new Promise((resolve, reject) => {
    const tx = db.transaction('data', 'readonly'); const get = tx.objectStore('data').get('history')
    get.onsuccess = () => resolve(get.result ?? [])
    tx.onerror = () => reject(new Error('读取本机历史失败')); tx.oncomplete = () => db.close()
  })
}
export async function addGuestHistory(history: History): Promise<void> {
  const db = await database()
  return new Promise((resolve, reject) => {
    const tx = db.transaction('data', 'readwrite'); const store = tx.objectStore('data'); const get = store.get('history')
    get.onsuccess = () => store.put([history, ...(get.result ?? [])].slice(0, 1000), 'history')
    tx.oncomplete = () => { db.close(); resolve() }
    tx.onabort = () => { db.close(); reject(new Error('本机历史保存失败，请检查浏览器存储空间')) }
  })
}
export async function importedGuestRevision(userId: string): Promise<number | undefined> {
  const db = await database()
  return new Promise((resolve, reject) => {
    const tx = db.transaction('data', 'readonly'); const get = tx.objectStore('data').get(`imported:${userId}`)
    get.onsuccess = () => resolve(get.result)
    tx.oncomplete = () => db.close(); tx.onerror = () => reject(new Error('读取导入状态失败'))
  })
}
export async function markGuestImported(userId: string, revision: number): Promise<void> {
  const db = await database()
  return new Promise((resolve, reject) => {
    const tx = db.transaction('data', 'readwrite'); tx.objectStore('data').put(revision, `imported:${userId}`)
    tx.oncomplete = () => { db.close(); resolve() }; tx.onabort = () => { db.close(); reject(new Error('记录导入状态失败')) }
  })
}
