import { createServer } from 'node:net'
import { timingSafeEqual } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join, resolve } from 'node:path'

export const name = 'dsh-windows-lifecycle'

const PROTOCOL_VERSION = 1
const MAX_INPUT_BYTES = 64 * 1024
const COMMANDS = new Set(['ping', 'getStatus', 'getRuntimeInfo', 'shutdown'])
const EVENT_NAMES = new Set(['ready', 'stopping', 'exiting'])

function matchesToken(actual, expected) {
  const left = Buffer.from(String(actual || ''), 'utf8')
  const right = Buffer.from(String(expected || ''), 'utf8')
  return left.length === right.length && left.length > 0 && timingSafeEqual(left, right)
}

function send(socket, message) {
  if (socket.destroyed || socket.writableEnded) return
  socket.write(`${JSON.stringify(message)}\n`)
}

function errorResponse(request, code, message, extra) {
  const response = {
    protocolVersion: PROTOCOL_VERSION,
    messageType: 'response',
    type: request && typeof request.type === 'string' ? request.type : 'error',
    ok: false,
    payload: null,
    error: { code, message, ...(extra || {}) },
  }
  if (request && typeof request.requestId === 'string' && request.requestId.length > 0) {
    response.requestId = request.requestId
  }
  return response
}

function successResponse(request, payload) {
  const response = {
    protocolVersion: PROTOCOL_VERSION,
    messageType: 'response',
    type: request && typeof request.type === 'string' ? request.type : 'error',
    ok: true,
    payload: payload || {},
    error: null,
  }
  if (request && typeof request.requestId === 'string' && request.requestId.length > 0) {
    response.requestId = request.requestId
  }
  return response
}

function eventMessage(eventName, payload) {
  return {
    protocolVersion: PROTOCOL_VERSION,
    messageType: 'event',
    type: eventName,
    payload: payload || {},
    error: null,
  }
}

function readDshVersion() {
  try {
    const entry = process.argv && process.argv[1]
    if (typeof entry !== 'string' || entry.length === 0) return null
    let current = dirname(entry)
    for (let depth = 0; depth < 8 && current; depth += 1) {
      try {
        const manifest = JSON.parse(readFileSync(join(current, 'package.json'), 'utf8'))
        if (manifest && manifest.name === '@deepseek-ai/dsh' && typeof manifest.version === 'string') {
          return manifest.version
        }
      } catch {
        // Continue walking toward the DSH install/source root.
      }
      const parent = dirname(current)
      if (parent === current) break
      current = parent
    }
  } catch {
    // Version is optional. The Manager retains its own installed-version path.
  }
  return null
}

function getWebServer(ctx) {
  try {
    const server = ctx.get('webServer')
    if (server && typeof server === 'object') return server
  } catch {
    // The service is absent until the Web composition mounts it.
  }
  return null
}

function getBoundPort(ctx) {
  const server = getWebServer(ctx)
  if (!server) return null
  const port = server.port
  return typeof port === 'number' && Number.isFinite(port) && port > 0 && port <= 65535 ? port : null
}

function resolveDshHome(ctx) {
  let selected = null
  const launchEnvironment = ctx.get('launchEnvironment')
  if (launchEnvironment && typeof launchEnvironment.get === 'function') {
    const entry = launchEnvironment.get('DSH_HOME')
    if (entry && typeof entry.value === 'string' && entry.value.trim().length > 0) selected = entry.value
  }
  if (selected === null && process.env.DSH_HOME && process.env.DSH_HOME.trim().length > 0) selected = process.env.DSH_HOME
  if (selected === null) return join(homedir(), '.dsh')
  try {
    if (selected === '~') return homedir()
    if (selected.startsWith('~/') || selected.startsWith('~\\')) return resolve(join(homedir(), selected.slice(2)))
    return resolve(selected)
  } catch {
    return selected
  }
}

function runtimeInfo(ctx, config) {
  const server = getWebServer(ctx)
  const port = getBoundPort(ctx)
  let dshHome = null
  try {
    dshHome = resolveDshHome(ctx)
  } catch {
    dshHome = null
  }
  return {
    protocolVersion: PROTOCOL_VERSION,
    state: port === null ? 'starting' : 'ready',
    pid: process.pid,
    port,
    host: server && typeof server.host === 'string' ? server.host : (port === null ? null : '127.0.0.1'),
    dshVersion: readDshVersion(),
    profile: config && typeof config.profile === 'string' && config.profile.length > 0 ? config.profile : null,
    dshHome,
    nodeVersion: process.version,
    cwd: process.cwd(),
  }
}

function validateMessage(request) {
  if (!request || typeof request !== 'object' || Array.isArray(request)) return 'malformed-message'
  if (request.protocolVersion !== PROTOCOL_VERSION) return 'protocol-version-unsupported'
  if (request.messageType !== 'command') return 'malformed-message'
  if (typeof request.type !== 'string' || !COMMANDS.has(request.type)) return 'unknown-command'
  return null
}

export async function apply(ctx, config = {}) {
  config = config || {}
  const exit = ctx.get('appExit')
  const pipeName = String(config.pipeName || process.env.DSH_WINDOWS_MANAGER_PIPE_NAME || '')
  const token = String(config.token || process.env.DSH_WINDOWS_MANAGER_TOKEN || '')

  // When installed as a normal DSH bundle there is no per-launch pipe and
  // token yet. Stay inert instead of opening an unauthenticated pipe.
  if (pipeName === '' && token === '') return
  if (typeof exit !== 'function') {
    throw new Error('dsh-windows-lifecycle: ctx.appExit is unavailable')
  }
  if (!/^[A-Za-z0-9._-]{1,128}$/.test(pipeName)) {
    throw new Error('dsh-windows-lifecycle: invalid pipeName')
  }
  if (!/^[a-f0-9]{64}$/.test(token)) {
    throw new Error('dsh-windows-lifecycle: a 256-bit hexadecimal token is required')
  }

  const pipePath = process.platform === 'win32'
    ? `\\\\.\\pipe\\${pipeName}`
    : `${process.env.TMPDIR || '/tmp'}/${pipeName}.sock`
  let shutdownRequested = false
  let disposed = false
  let readyEmitted = false
  let readinessTimer = null
  let readinessChecks = 0
  const sockets = new Set()
  const authenticatedSockets = new Set()

  const statePayload = () => {
    const info = runtimeInfo(ctx, config)
    return shutdownRequested ? { ...info, state: 'stopping' } : info
  }

  const broadcast = (eventName, extra) => {
    if (!EVENT_NAMES.has(eventName)) return
    const message = eventMessage(eventName, { ...statePayload(), ...(extra || {}) })
    for (const socket of authenticatedSockets) send(socket, message)
  }

  const emitReady = () => {
    if (disposed || shutdownRequested || readyEmitted) return
    if (getBoundPort(ctx) === null) return
    readyEmitted = true
    broadcast('ready')
  }

  const scheduleReadinessCheck = (delayMs) => {
    if (disposed || readinessChecks >= 360) return
    readinessChecks += 1
    if (readinessTimer) clearTimeout(readinessTimer)
    readinessTimer = setTimeout(() => {
      readinessTimer = null
      if (disposed || shutdownRequested) return
      if (getBoundPort(ctx) !== null) {
        emitReady()
        return
      }
      scheduleReadinessCheck(250)
    }, delayMs)
    if (readinessTimer.unref) readinessTimer.unref()
  }

  const exitAfterFlush = () => {
    const targets = [...authenticatedSockets]
    if (targets.length === 0) {
      setImmediate(() => exit(0))
      return
    }
    const safety = setTimeout(() => exit(0), 1000)
    if (safety.unref) safety.unref()
    let remaining = targets.length
    const finish = () => {
      remaining -= 1
      if (remaining <= 0) {
        clearTimeout(safety)
        setImmediate(() => exit(0))
      }
    }
    for (const socket of targets) {
      if (socket.destroyed || socket.writableEnded) finish()
      else socket.end('', finish)
    }
  }

  const handleCommand = (socket, request) => {
    const validationError = validateMessage(request)
    if (validationError === 'protocol-version-unsupported') {
      send(socket, errorResponse(request, 'protocol-version-unsupported',
        `Unsupported protocol version. This plugin speaks version ${PROTOCOL_VERSION}.`,
        { supportedProtocolVersion: PROTOCOL_VERSION }))
      return
    }
    if (validationError !== null) {
      send(socket, errorResponse(request, validationError, 'The request is malformed or unsupported.'))
      return
    }
    if (!matchesToken(request.token, token)) {
      send(socket, errorResponse(request, 'unauthorized', 'The request token is missing or incorrect.'))
      return
    }
    authenticatedSockets.add(socket)

    if (request.type === 'ping') {
      send(socket, successResponse(request, { pong: true }))
      return
    }
    if (request.type === 'getStatus') {
      send(socket, successResponse(request, statePayload()))
      return
    }
    if (request.type === 'getRuntimeInfo') {
      send(socket, successResponse(request, statePayload()))
      return
    }
    if (request.type === 'shutdown') {
      if (shutdownRequested) {
        send(socket, successResponse(request, { alreadyRequested: true }))
        return
      }
      shutdownRequested = true
      send(socket, successResponse(request, { alreadyRequested: false }))
      broadcast('stopping', { reason: 'manager-requested-shutdown' })
      broadcast('exiting', { reason: 'manager-requested-shutdown', state: 'exiting' })
      exitAfterFlush()
    }
  }

  const server = createServer((socket) => {
    sockets.add(socket)
    socket.setEncoding('utf8')
    socket.setTimeout(5000, () => socket.destroy())
    let input = ''
    socket.on('data', (chunk) => {
      input += chunk
      if (input.length > MAX_INPUT_BYTES) {
        send(socket, errorResponse(null, 'message-too-large', 'The message exceeds the configured size limit.'))
        socket.destroy()
        return
      }
      let newline = input.indexOf('\n')
      while (newline >= 0 && !socket.destroyed) {
        const line = input.slice(0, newline)
        input = input.slice(newline + 1)
        if (line.trim().length > 0) {
          let request
          try {
            request = JSON.parse(line)
          } catch {
            send(socket, errorResponse(null, 'invalid-json', 'The request is not valid JSON.'))
            socket.end()
            return
          }
          handleCommand(socket, request)
          if (authenticatedSockets.has(socket) && !socket.destroyed) socket.setTimeout(0)
        }
        newline = input.indexOf('\n')
      }
    })
    socket.on('close', () => {
      sockets.delete(socket)
      authenticatedSockets.delete(socket)
    })
    socket.on('error', () => {
      sockets.delete(socket)
      authenticatedSockets.delete(socket)
    })
  })

  await new Promise((resolve, reject) => {
    const onError = (error) => {
      server.off('listening', onListening)
      reject(error)
    }
    const onListening = () => {
      server.off('error', onError)
      resolve()
    }
    server.once('error', onError)
    server.once('listening', onListening)
    server.listen(pipePath)
  })

  scheduleReadinessCheck(0)

  ctx.effect(() => () => new Promise((resolve) => {
    if (disposed) {
      resolve()
      return
    }
    disposed = true
    if (readinessTimer) clearTimeout(readinessTimer)
    broadcast('stopping', { reason: 'cordis-dispose' })
    for (const socket of sockets) {
      if (!socket.destroyed && !socket.writableEnded) socket.end()
    }
    if (!server.listening) {
      resolve()
      return
    }
    server.close(() => resolve())
  }), 'dsh-windows-lifecycle: named pipe')
}

