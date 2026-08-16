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
    socket.on('data', (chunk) => {
      response += chunk
      if (response.includes('\n')) {
        socket.destroy()
        resolve(response)
      }
    })
    socket.once('end', () => resolve(response))
    socket.once('error', reject)
  })
}

try {
  await apply(ctx, { pipeName, token })

  const unauthorized = await request({ protocolVersion: 1, messageType: 'command', requestId: 'r1', type: 'getStatus', token: '0'.repeat(64), payload: {} })
  if (!unauthorized.includes('unauthorized')) throw new Error('wrong token was not rejected')
  if (exitCode !== undefined) throw new Error('wrong token triggered appExit')

  const ping = await request({ protocolVersion: 1, messageType: 'command', requestId: 'r2', type: 'ping', token, payload: {} })
  const pingMessage = JSON.parse(ping)
  if (!pingMessage.ok || pingMessage.payload.pong !== true) throw new Error('versioned ping was not accepted')

  const status = await request({ protocolVersion: 1, messageType: 'command', requestId: 'r3', type: 'getStatus', token, payload: {} })
  const statusMessage = JSON.parse(status)
  if (!statusMessage.ok || statusMessage.payload.pid !== process.pid) throw new Error('versioned getStatus did not return the DSH PID')
  if (!['starting', 'ready'].includes(statusMessage.payload.state)) throw new Error('unexpected DSH state')

  const shutdown = await request({ protocolVersion: 1, messageType: 'command', requestId: 'r4', type: 'shutdown', token, payload: {} })
  const shutdownMessage = JSON.parse(shutdown)
  if (!shutdownMessage.ok) throw new Error('versioned shutdown was not accepted')
  await Promise.race([exitPromise, new Promise((_, reject) => setTimeout(() => reject(new Error('appExit timeout')), 3000))])
  if (exitCode !== 0) throw new Error(`unexpected exit code ${exitCode}`)
  console.log('PASS named-pipe protocol, authentication, status, and versioned appExit')
} finally {
  if (typeof disposer === 'function') await disposer()
}
