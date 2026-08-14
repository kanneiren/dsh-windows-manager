import { createServer } from 'node:net'
import { timingSafeEqual } from 'node:crypto'

export const name = 'dsh-windows-lifecycle'

function matchesToken(actual, expected) {
  const left = Buffer.from(String(actual || ''), 'utf8')
  const right = Buffer.from(String(expected || ''), 'utf8')
  return left.length === right.length && left.length > 0 && timingSafeEqual(left, right)
}

export async function apply(ctx, config = {}) {
  const exit = ctx.get('appExit')
  if (typeof exit !== 'function') {
    throw new Error('dsh-windows-lifecycle: ctx.appExit is unavailable')
  }

  const pipeName = String(config.pipeName || '')
  const token = String(config.token || '')
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
  const sockets = new Set()
  const server = createServer((socket) => {
    sockets.add(socket)
    socket.setEncoding('utf8')
    socket.setTimeout(2000, () => socket.destroy())
    let input = ''
    socket.on('data', (chunk) => {
      input += chunk
      if (input.length > 4096) {
        socket.destroy()
        return
      }
      const newline = input.indexOf('\n')
      if (newline < 0) return

      let request
      try {
        request = JSON.parse(input.slice(0, newline))
      } catch {
        socket.end('{"ok":false,"error":"invalid-json"}\n')
        return
      }
      if (request.action !== 'shutdown' || !matchesToken(request.token, token)) {
        socket.end('{"ok":false,"error":"unauthorized"}\n')
        return
      }
      if (shutdownRequested) {
        socket.end('{"ok":true,"alreadyRequested":true}\n')
        return
      }
      shutdownRequested = true
      socket.end('{"ok":true}\n', () => setImmediate(() => exit(0)))
    })
    socket.on('close', () => sockets.delete(socket))
    socket.on('error', () => sockets.delete(socket))
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

  ctx.effect(() => () => new Promise((resolve) => {
    for (const socket of sockets) socket.destroy()
    if (!server.listening) {
      resolve()
      return
    }
    server.close(() => resolve())
  }), 'dsh-windows-lifecycle: named pipe')
}
