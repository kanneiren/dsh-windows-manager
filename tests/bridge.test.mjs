import { connect } from 'node:net'
import { randomBytes } from 'node:crypto'
import { apply } from '../plugins/deepseek-harness-web/cordis/windows-lifecycle.mjs'

const pipeName = `DeepSeekHarnessManager-test-${process.pid}-${Date.now()}`
const token = randomBytes(32).toString('hex')
let disposer
let exitCode
let exitResolve
const exitPromise = new Promise((resolve) => { exitResolve = resolve })
const ctx = {
  get(name) {
    if (name !== 'appExit') return undefined
    return (code) => {
      exitCode = code
      exitResolve()
    }
  },
  effect(register) {
    disposer = register()
  },
}

function request(value) {
  const path = process.platform === 'win32' ? `\\\\.\\pipe\\${pipeName}` : `${process.env.TMPDIR || '/tmp'}/${pipeName}.sock`
  return new Promise((resolve, reject) => {
    const socket = connect(path)
    let response = ''
    socket.setEncoding('utf8')
    socket.once('connect', () => socket.write(`${JSON.stringify(value)}\n`))
    socket.on('data', (chunk) => { response += chunk })
    socket.once('end', () => resolve(response))
    socket.once('error', reject)
  })
}

try {
  await apply(ctx, { pipeName, token })
  const rejected = await request({ action: 'shutdown', token: '0'.repeat(64) })
  if (!rejected.includes('unauthorized')) throw new Error('wrong token was not rejected')
  if (exitCode !== undefined) throw new Error('wrong token triggered appExit')

  const accepted = await request({ action: 'shutdown', token })
  if (!accepted.includes('"ok":true')) throw new Error('correct token was not accepted')
  await Promise.race([exitPromise, new Promise((_, reject) => setTimeout(() => reject(new Error('appExit timeout')), 3000))])
  if (exitCode !== 0) throw new Error(`unexpected exit code ${exitCode}`)
  console.log('PASS named-pipe bridge authentication and appExit')
} finally {
  if (typeof disposer === 'function') await disposer()
}
