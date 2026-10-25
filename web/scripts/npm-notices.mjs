import { spawnSync } from 'node:child_process'
import {
  mkdirSync,
  readFileSync,
  realpathSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { licenseAllowed } from './license-policy.mjs'

const scriptDir = dirname(fileURLToPath(import.meta.url))
const webRoot = resolve(scriptDir, '..')
const repoRoot = resolve(webRoot, '..')

export const repoInputs = ['build/allowed-licenses.json']

function option(flag) {
  const index = process.argv.indexOf(flag)
  if (index === -1) return undefined
  const next = process.argv[index + 1]
  if (!next || next.startsWith('-')) {
    console.error(`Missing value for ${flag}`)
    process.exit(1)
  }
  return next
}

function remove(path) {
  try {
    unlinkSync(path)
  } catch (error) {
    if (error.code !== 'ENOENT') throw error
  }
}

function readAllowed() {
  let data
  try {
    data = JSON.parse(
      readFileSync(join(repoRoot, ...repoInputs[0].split('/')), 'utf8'),
    )
  } catch {
    console.error('Allowed license list is missing or invalid.')
    process.exit(1)
  }
  if (
    !Array.isArray(data) ||
    data.length === 0 ||
    data.some((entry) => typeof entry !== 'string' || entry.trim() === '')
  ) {
    console.error('Allowed license list is missing or invalid.')
    process.exit(1)
  }
  return data
}

function licenseExpression(manifest) {
  const { license, licenses } = manifest
  if (typeof license === 'string') return license.trim()
  if (
    license &&
    typeof license === 'object' &&
    typeof license.type === 'string'
  )
    return license.type.trim()
  if (!Array.isArray(licenses) || licenses.length === 0) return ''
  return licenses
    .map((entry) => {
      const id = typeof entry === 'string' ? entry : entry?.type
      return typeof id === 'string' ? `(${id.trim()})` : ''
    })
    .join(' AND ')
}

function fail(out, lines) {
  remove(out)
  for (const line of lines) console.error(line)
  process.exit(1)
}

function listTree(start) {
  const result = spawnSync('npm ls --omit=dev --all --json --long', {
    cwd: start,
    encoding: 'utf8',
    shell: true,
    maxBuffer: 256 * 1024 * 1024,
  })
  let tree
  try {
    tree = JSON.parse(result.stdout)
  } catch {
    return {
      error: result.stderr?.trim() || 'npm ls produced no usable output.',
    }
  }
  if (result.status !== 0) {
    return { error: result.stderr?.trim() || 'npm ls reported problems.' }
  }
  return { tree }
}

function collect(node, found) {
  for (const child of Object.values(node.dependencies ?? {})) {
    if (child.path && !found.has(child.path)) {
      found.set(child.path, child)
      collect(child, found)
    }
  }
  return found
}

function main() {
  const start = resolve(option('--start') ?? webRoot)
  const out = resolve(
    option('--out') ?? join(webRoot, 'public', 'licenses', 'npm.json'),
  )
  const allowed = readAllowed()
  remove(out)

  const { tree, error } = listTree(start)
  if (error) fail(out, [error])

  const packages = new Map()
  const bad = []
  for (const info of collect(tree, new Map()).values()) {
    if (info.private === true) continue
    const name = typeof info.name === 'string' ? info.name : ''
    const version = typeof info.version === 'string' ? info.version : ''
    if (!name || !version) {
      bad.push(`${info.path} unreadable package name`)
      continue
    }
    const license = licenseExpression(info)
    if (!license || !licenseAllowed(license, allowed)) {
      bad.push(`${name}@${version} ${license || 'UNKNOWN'}`)
      continue
    }
    packages.set(`${name}@${version}`, { name, version, license })
  }
  if (bad.length > 0) {
    fail(out, ['Incompatible or unknown licenses:', ...bad])
  }
  if (packages.size === 0) {
    fail(out, ['No production packages were found.'])
  }
  const sorted = [...packages.values()].sort(
    (left, right) =>
      left.name.localeCompare(right.name) ||
      left.version.localeCompare(right.version) ||
      left.license.localeCompare(right.license),
  )
  mkdirSync(dirname(out), { recursive: true })
  writeFileSync(
    out,
    `${JSON.stringify({ packages: sorted }, null, 2)}
`,
  )
}

const entry = process.argv[1]
if (
  entry &&
  realpathSync(entry) === realpathSync(fileURLToPath(import.meta.url))
) {
  main()
}
