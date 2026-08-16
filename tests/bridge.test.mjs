import { connect, createServer } from 'node:net'
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

function freeTcpPort() {
  return new Promise((resolve) => {
    const server = createServer()
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address()
      server.close(() => resolve(port))
    })
  })
}

function requestTcp(port, value) {
  return new Promise((resolve, reject) => {
    const socket = connect(port, '127.0.0.1')
    let response = ''
    socket.setEncoding('utf8')
    socket.once('connect', () => socket.write(`${JSON.stringify(value)}\n`))
    socket.on('data', (chunk) => {
      response += chunk
      while (response.includes('\n')) {
        const newline = response.indexOf('\n')
        const line = response.slice(0, newline)
        response = response.slice(newline + 1)
        if (!line) continue
        let parsed = null
        try { parsed = JSON.parse(line) } catch (_) { parsed = null }
        if (parsed && parsed.messageType === 'response') {
          socket.destroy()
          resolve(line)
          return
        }
      }
    })
    socket.once('end', () => resolve(response))
    socket.once('error', reject)
  })
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
      while (response.includes('\n')) {
        const newline = response.indexOf('\n')
        const line = response.slice(0, newline)
        response = response.slice(newline + 1)
        if (!line) continue
        let parsed = null
        try { parsed = JSON.parse(line) } catch (_) { parsed = null }
        if (parsed && parsed.messageType === 'response') {
          socket.destroy()
          resolve(line)
          return
        }
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

const tcpPort = await freeTcpPort()
const tcpToken = randomBytes(32).toString('hex')
let tcpDisposer
let tcpExitCode
let tcpExitResolve
const tcpExitPromise = new Promise((resolve) => { tcpExitResolve = resolve })
const tcpCtx = {
  get(name) {
    if (name !== 'appExit') return undefined
    return (code) => {
      tcpExitCode = code
      tcpExitResolve()
    }
  },
  effect(register) {
    tcpDisposer = register()
  },
}
try {
  await apply(tcpCtx, { transport: 'tcp', host: '127.0.0.1', port: tcpPort, token: tcpToken })
  const tcpUnauthorized = await requestTcp(tcpPort, { protocolVersion: 1, messageType: 'command', requestId: 't0', type: 'getStatus', token: '0'.repeat(64), payload: {} })
  if (!tcpUnauthorized.includes('unauthorized')) throw new Error('TCP wrong token was not rejected')
  const tcpStatus = await requestTcp(tcpPort, { protocolVersion: 1, messageType: 'command', requestId: 't1', type: 'getStatus', token: tcpToken, payload: {} })
  const tcpStatusMessage = JSON.parse(tcpStatus)
  if (!tcpStatusMessage.ok || tcpStatusMessage.payload.pid !== process.pid) throw new Error('TCP getStatus did not return the DSH PID')
  const tcpPing = await requestTcp(tcpPort, { protocolVersion: 1, messageType: 'command', requestId: 't2', type: 'ping', token: tcpToken, payload: {} })
  const tcpPingMessage = JSON.parse(tcpPing)
  if (!tcpPingMessage.ok || tcpPingMessage.payload.pong !== true) throw new Error('TCP ping was not accepted')
  const tcpShutdown = await requestTcp(tcpPort, { protocolVersion: 1, messageType: 'command', requestId: 't3', type: 'shutdown', token: tcpToken, payload: {} })
  if (!JSON.parse(tcpShutdown).ok) throw new Error('TCP shutdown was not accepted')
  await Promise.race([tcpExitPromise, new Promise((_, reject) => setTimeout(() => reject(new Error('TCP appExit timeout')), 3000))])
  if (tcpExitCode !== 0) throw new Error(`unexpected TCP exit code ${tcpExitCode}`)
  console.log('PASS TCP runtime-bridge transport, authentication, and appExit')
} finally {
  if (typeof tcpDisposer === 'function') await tcpDisposer()
}
