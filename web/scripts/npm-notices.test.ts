import { execFileSync } from 'node:child_process'
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { expect, it, vi } from 'vitest'
import { repoInputs } from './npm-notices.mjs'

vi.setConfig({ testTimeout: 30_000 })

const web = join(__dirname, '..')
const repo = join(web, '..')

function run(args: string[]) {
  return execFileSync(process.execPath, ['scripts/npm-notices.mjs', ...args], {
    cwd: web,
    encoding: 'utf8',
  })
}

function fixture(packages: Record<string, { license?: string }>) {
  const dir = mkdtempSync(join(tmpdir(), 'wishlistarr-npm-'))
  const dependencies: Record<string, string> = {}
  for (const name of Object.keys(packages)) dependencies[name] = '1.0.0'
  writeFileSync(
    join(dir, 'package.json'),
    JSON.stringify({
      name: 'notice-fixture',
      private: true,
      version: '1.0.0',
      license: 'MIT',
      dependencies,
    }),
  )
  for (const [name, info] of Object.entries(packages)) {
    const folder = join(dir, 'node_modules', name)
    mkdirSync(folder, { recursive: true })
    const manifest: { name: string; version: string; license?: string } = {
      name,
      version: '1.0.0',
    }
    if ('license' in info) manifest.license = info.license
    writeFileSync(join(folder, 'package.json'), JSON.stringify(manifest))
  }
  return dir
}

function failure(args: string[]) {
  try {
    run(args)
  } catch (error) {
    const thrown = error as { status?: number; stderr?: unknown }
    return {
      status: thrown.status ?? 0,
      stderr: String(thrown.stderr ?? ''),
    }
  }
  return { status: 0, stderr: '' }
}

it('writes production notices for this app', () => {
  run([])
  const text = readFileSync(join(web, 'public', 'licenses', 'npm.json'), 'utf8')
  expect(text).not.toMatch(/licenseFile|"path"/)
  const doc = JSON.parse(text) as {
    packages: { name: string; license: string }[]
  }
  expect(doc.packages.find((pkg) => pkg.name === 'react')?.license).toBe('MIT')
  expect(
    doc.packages.find((pkg) => pkg.name === '@fontsource-variable/fredoka')
      ?.license,
  ).toBe('OFL-1.1')
}, 30_000)

it('rejects unknown and gpl-2.0-only packages and removes any previous file', () => {
  const dir = fixture({
    'bad-pkg': { license: 'GPL-2.0-only' },
    'empty-pkg': {},
  })
  const out = join(dir, 'notices.json')
  writeFileSync(
    out,
    '{"packages":[{"name":"stale","version":"0","license":"MIT"}]}',
  )
  try {
    const result = failure(['--start', dir, '--out', out])
    expect(result.status).not.toBe(0)
    expect(result.stderr).toContain('bad-pkg@1.0.0 GPL-2.0-only')
    expect(result.stderr).toContain('empty-pkg@1.0.0 UNKNOWN')
    expect(existsSync(out)).toBe(false)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

it('accepts a dependency that offers an allowed license', () => {
  const dir = fixture({ 'or-pkg': { license: '(MIT OR GPL-2.0-only)' } })
  const out = join(dir, 'notices.json')
  try {
    run(['--start', dir, '--out', out])
    const doc = JSON.parse(readFileSync(out, 'utf8')) as {
      packages: { name: string; version: string; license: string }[]
    }
    expect(doc.packages).toEqual([
      { name: 'or-pkg', version: '1.0.0', license: '(MIT OR GPL-2.0-only)' },
    ])
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

it('fails closed when a tree has no production packages', () => {
  const dir = fixture({})
  const out = join(dir, 'notices.json')
  try {
    const result = failure(['--start', dir, '--out', out])
    expect(result.status).not.toBe(0)
    expect(result.stderr).toContain('No production packages were found.')
    expect(existsSync(out)).toBe(false)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

it('keeps generated notices out of git and runs the check before the build', () => {
  expect(readFileSync(join(repo, '.gitignore'), 'utf8')).toContain(
    'web/public/licenses/',
  )
  const pkg = JSON.parse(readFileSync(join(web, 'package.json'), 'utf8')) as {
    scripts: { prebuild: string; predev: string }
  }
  expect(pkg.scripts.prebuild).toContain('scripts/npm-notices.mjs')
  expect(pkg.scripts.predev).toContain('scripts/npm-notices.mjs')
})

it('copies every repository file the build reads into the Docker web stage', () => {
  const dockerfile = readFileSync(join(repo, 'Dockerfile'), 'utf8')
  const stage = dockerfile
    .split(/^FROM /m)
    .find((part) => /\bAS web\b/.test(part))
  expect(stage).toBeDefined()
  const copies = [...(stage ?? '').matchAll(/^COPY (\S+) (\S+)$/gm)].map(
    (match) => ({ source: match[1], target: match[2] }),
  )
  for (const input of repoInputs) {
    expect(copies, input).toContainEqual({
      source: input,
      target: `/src/${input}`,
    })
  }
})
