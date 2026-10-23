import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { expect, it } from 'vitest'
import { createTranslator } from '.'
import { en } from './en'

it('fills in parameters', () => {
  const t = createTranslator('en')
  expect(t('home.welcome', { name: 'Ada' })).toBe('Welcome, Ada!')
})

it('falls back to English for unknown locales', () => {
  expect(createTranslator('xx')('picker.title')).toBe('Who is watching?')
})

it('leaves unknown placeholders alone', () => {
  expect(createTranslator('en')('home.welcome')).toBe('Welcome, {name}!')
})

function sources(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name)
    if (entry.isDirectory()) return sources(path)
    return /\.tsx?$/.test(entry.name) && !/\.test\./.test(entry.name)
      ? [path]
      : []
  })
}

it('only uses keys that exist in the catalog', () => {
  const used = new Set<string>()
  for (const file of sources(join(__dirname, '..'))) {
    for (const match of readFileSync(file, 'utf8').matchAll(
      /\bt\(\s*'([a-z]+\.[A-Za-z]+)'/g,
    )) {
      used.add(match[1])
    }
  }
  expect(used.size).toBeGreaterThan(20)
  for (const key of used) {
    expect(Object.keys(en)).toContain(key)
  }
})

it('keeps visible text out of the picker and shell components', () => {
  for (const name of [
    'Shell.tsx',
    'kid/ProfilePicker.tsx',
    'kid/PinPad.tsx',
    'kid/KidHome.tsx',
    'kid/Wishlist.tsx',
  ]) {
    const text = readFileSync(join(__dirname, '..', name), 'utf8')
    expect(text, name).not.toMatch(/(?<![=-])>\s*[A-Z][a-z][A-Za-z ,.!?']*<\//)
  }
})
