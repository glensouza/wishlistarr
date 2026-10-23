import {
  mkdirSync,
  readFileSync,
  realpathSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { init } from 'license-checker-rseidelsohn'
import { licenseAllowed } from './license-policy.mjs'

const scriptDir = dirname(fileURLToPath(import.meta.url))
const webRoot = resolve(scriptDir, '..')
const repoRoot = resolve(webRoot, '..')

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
      readFileSync(join(repoRoot, 'build', 'allowed-licenses.json'), 'utf8'),
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

function splitPackage(key) {
  const at = key.lastIndexOf('@')
  if (at <= 0) return null
  const name = key.slice(0, at)
  const version = key.slice(at + 1)
  if (!name || !version) return null
  return { name, version }
}

function licenseExpression(licenses) {
  if (typeof licenses === 'string') return licenses.trim()
  if (!Array.isArray(licenses) || licenses.length === 0) return ''
  return licenses
    .map((entry) => (typeof entry === 'string' ? `(${entry.trim()})` : ''))
    .join(' AND ')
}

function fail(out, lines) {
  remove(out)
  for (const line of lines) console.error(line)
  process.exit(1)
}

function main() {
  const start = resolve(option('--start') ?? webRoot)
  const out = resolve(
    option('--out') ?? join(webRoot, 'public', 'licenses', 'npm.json'),
  )
  const allowed = readAllowed()
  remove(out)

  init(
    {
      start,
      production: true,
      excludePrivatePackages: true,
      unknown: true,
    },
    (err, ret) => {
      if (err) fail(out, [err.message ?? String(err)])
      const packages = []
      const bad = []
      for (const [key, info] of Object.entries(ret ?? {})) {
        const parsed = splitPackage(key)
        const license = licenseExpression(info?.licenses)
        if (!parsed) {
          bad.push(`${key} unreadable package name`)
          continue
        }
        if (!license || !licenseAllowed(license, allowed)) {
          bad.push(`${parsed.name}@${parsed.version} ${license || 'UNKNOWN'}`)
          continue
        }
        packages.push({
          name: parsed.name,
          version: parsed.version,
          license,
        })
      }
      if (bad.length > 0) {
        fail(out, ['Incompatible or unknown licenses:', ...bad])
      }
      if (packages.length === 0) {
        fail(out, ['No production packages were found.'])
      }
      packages.sort(
        (left, right) =>
          left.name.localeCompare(right.name) ||
          left.version.localeCompare(right.version) ||
          left.license.localeCompare(right.license),
      )
      mkdirSync(dirname(out), { recursive: true })
      writeFileSync(out, `${JSON.stringify({ packages }, null, 2)}\n`)
    },
  )
}

const entry = process.argv[1]
if (
  entry &&
  realpathSync(entry) === realpathSync(fileURLToPath(import.meta.url))
) {
  main()
}
