import net from 'node:net'
import { spawn } from 'node:child_process'

/** Check whether a local development service is already listening. */
function isListening(port) {
  return new Promise((resolve) => {
    const socket = net.connect({ host: '127.0.0.1', port })
    socket.setTimeout(700)
    socket.once('connect', () => {
      socket.destroy()
      resolve(true)
    })
    socket.once('timeout', () => {
      socket.destroy()
      resolve(false)
    })
    socket.once('error', () => resolve(false))
  })
}

/** Stop a child process and its descendants when the desktop app exits. */
function stopTree(child) {
  if (!child.pid) return
  if (process.platform === 'win32') {
    spawn('taskkill', ['/pid', String(child.pid), '/t', '/f'], { stdio: 'ignore' })
  } else {
    child.kill('SIGTERM')
  }
}

const children = []
let stopping = false

/** Stop all services launched by this script. */
function stopAll() {
  if (stopping) return
  stopping = true
  for (const child of children) stopTree(child)
}

/** Launch a command and forward its output to the current terminal. */
function launch(command, args) {
  const child = spawn(command, args, { stdio: 'inherit' })
  children.push(child)
  child.once('error', (error) => {
    console.error(`Failed to start ${command}: ${error.message}`)
    process.exitCode = 1
    stopAll()
  })
  child.once('exit', (code) => {
    if (!stopping && code !== 0) {
      process.exitCode = code ?? 1
      stopAll()
    }
  })
  return child
}

/** Start only the local services that are not already running. */
async function main() {
  if (!(await isListening(5173))) {
    launch(process.execPath, ['./node_modules/vite/bin/vite.js', '--host', '127.0.0.1'])
  }

  if (!(await isListening(5008))) {
    launch('dotnet', [
      'run',
      '--project',
      '../AgentBackend/AgentBackend/AgentBackend/AgentBackend.csproj',
      '--no-launch-profile',
      '--urls',
      'http://127.0.0.1:5008',
    ])
  }

  if (children.length === 0) return
  process.once('SIGINT', stopAll)
  process.once('SIGTERM', stopAll)
}

main().catch((error) => {
  console.error(error)
  process.exitCode = 1
  stopAll()
})
