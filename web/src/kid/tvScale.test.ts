import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { expect, it } from 'vitest'
import { kidScaleAttribute } from './spatialNav'

const styles = readFileSync(
  join(dirname(fileURLToPath(import.meta.url)), '../styles.css'),
  'utf8',
)

const initialFontPx = 16

function splitArgs(value: string) {
  const parts: string[] = []
  let depth = 0
  let current = ''
  for (const char of value) {
    if (char === '(') depth += 1
    if (char === ')') depth -= 1
    if (char === ',' && depth === 0) {
      parts.push(current.trim())
      current = ''
      continue
    }
    current += char
  }
  if (current.trim()) parts.push(current.trim())
  return parts
}

function resolveLength(value: string, viewport: number): number {
  const term = value.trim()
  const clamp = /^clamp\(\s*(.+)\s*\)$/.exec(term)
  if (clamp) {
    const parts = splitArgs(clamp[1]).map((part) =>
      resolveLength(part, viewport),
    )
    if (parts.length !== 3) throw new Error(term)
    const [min, preferred, max] = parts
    return Math.min(Math.max(preferred, min), max)
  }
  if (term.includes('+')) {
    return term
      .split('+')
      .reduce((sum, part) => sum + resolveLength(part, viewport), 0)
  }
  if (term.endsWith('rem')) return Number.parseFloat(term) * initialFontPx
  if (term.endsWith('vw')) return (Number.parseFloat(term) * viewport) / 100
  if (term.endsWith('px')) return Number.parseFloat(term)
  throw new Error(`unsupported length ${term}`)
}

function scaleRules(css: string) {
  const pattern = new RegExp(
    String.raw`@media\s*\(\s*min-width:\s*(\d+)px\s*\)\s*\{\s*html\[${kidScaleAttribute}\]\s*\{\s*font-size:\s*([^;]+);`,
    'g',
  )
  return [...css.matchAll(pattern)].map((match) => ({
    minWidth: Number(match[1]),
    fontSize: match[2].trim(),
  }))
}

function fontSizeAt(
  rules: { minWidth: number; fontSize: string }[],
  viewport: number,
) {
  const active = rules.filter((rule) => viewport >= rule.minWidth)
  if (active.length === 0) return initialFontPx
  const winner = active.reduce((best, rule) =>
    rule.minWidth >= best.minWidth ? rule : best,
  )
  return resolveLength(winner.fontSize, viewport)
}

it('scales kid screens up from a 1080p base to a larger 4K base', () => {
  const rules = scaleRules(styles)
  expect(rules.map((rule) => rule.minWidth)).toEqual(
    expect.arrayContaining([1920, 3840]),
  )
  const hd = fontSizeAt(rules, 1920)
  const uhd = fontSizeAt(rules, 3840)
  expect(hd).toBeGreaterThan(initialFontPx)
  expect(uhd).toBeGreaterThan(hd)
  expect(fontSizeAt(rules, 1280)).toBe(initialFontPx)
})
