import { spawn } from 'node:child_process'
import { mkdir, copyFile, chmod } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const frontendRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const repositoryRoot = path.resolve(frontendRoot, '..')
const backendProject = path.join(repositoryRoot, 'AgentBackend', 'AgentBackend', 'AgentBackend', 'AgentBackend.csproj')
const targetDirectory = path.join(frontendRoot, 'src-tauri', 'binaries')

const targets = {
  'win32-x64': { rid: 'win-x64', triple: 'x86_64-pc-windows-msvc', extension: '.exe' },
  'win32-arm64': { rid: 'win-arm64', triple: 'aarch64-pc-windows-msvc', extension: '.exe' },
  'darwin-x64': { rid: 'osx-x64', triple: 'x86_64-apple-darwin', extension: '' },
  'darwin-arm64': { rid: 'osx-arm64', triple: 'aarch64-apple-darwin', extension: '' },
  'linux-x64': { rid: 'linux-x64', triple: 'x86_64-unknown-linux-gnu', extension: '' },
  'linux-arm64': { rid: 'linux-arm64', triple: 'aarch64-unknown-linux-gnu', extension: '' },
}

const target = targets[`${process.platform}-${process.arch}`]
if (!target) {
  throw new Error(`Unsupported packaging platform: ${process.platform}-${process.arch}`)
}

const publishDirectory = path.join(repositoryRoot, 'AgentBackend', 'AgentBackend', 'AgentBackend', 'bin', 'desktop-publish', target.rid)

function run(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: repositoryRoot, stdio: 'inherit' })
    child.once('error', reject)
    child.once('exit', (code) => code === 0 ? resolve() : reject(new Error(`${command} exited with code ${code}`)))
  })
}

await run('dotnet', [
  'publish', backendProject,
  '--configuration', 'Release',
  '--runtime', target.rid,
  '--self-contained', 'true',
  '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true',
  '--output', publishDirectory,
])

const executableName = `AgentBackend${target.extension}`
const source = path.join(publishDirectory, executableName)
const destination = path.join(targetDirectory, `lucas-agent-backend-${target.triple}${target.extension}`)
await mkdir(targetDirectory, { recursive: true })
await copyFile(source, destination)
if (process.platform !== 'win32') await chmod(destination, 0o755)
console.log(`Prepared self-contained backend sidecar: ${destination}`)
