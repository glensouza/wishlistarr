import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { expect, it } from 'vitest'
import { spatialFocusClass } from '../kid/spatialNav'

const here = dirname(fileURLToPath(import.meta.url))
const tokens = readFileSync(join(here, 'tokens.css'), 'utf8')
const styles = readFileSync(join(here, '../styles.css'), 'utf8')

const adjacent = ['--color-bg', '--color-surface', '--color-surface-raised']

function parseColor(input: string): [number, number, number] {
  const value = input.trim()
  const hex = /^#([\da-f]{3}|[\da-f]{6})$/i.exec(value)
  if (hex) {
    const raw = hex[1]
    const full =
      raw.length === 3 ? [...raw].map((part) => part + part).join('') : raw
    const channels = Number.parseInt(full, 16)
    return [(channels >> 16) & 255, (channels >> 8) & 255, channels & 255]
  }
  const rgb = /^rgb\(\s*(\d+)[,\s]+(\d+)[,\s]+(\d+)\s*\)$/i.exec(value)
  if (!rgb) throw new Error(`unsupported color ${value}`)
  return [Number(rgb[1]), Number(rgb[2]), Number(rgb[3])]
}

function channel(value: number) {
  const scaled = value / 255
  return scaled <= 0.04045 ? scaled / 12.92 : ((scaled + 0.055) / 1.055) ** 2.4
}

function luminance(color: [number, number, number]) {
  return (
    0.2126 * channel(color[0]) +
    0.7152 * channel(color[1]) +
    0.0722 * channel(color[2])
  )
}

function contrast(foreground: string, background: string) {
  const lighter = Math.max(
    luminance(parseColor(foreground)),
    luminance(parseColor(background)),
  )
  const darker = Math.min(
    luminance(parseColor(foreground)),
    luminance(parseColor(background)),
  )
  return (lighter + 0.05) / (darker + 0.05)
}

function themeBlocks(css: string) {
  return [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)]
    .map((match) => ({
      selector: match[1].trim(),
      values: new Map(
        [...match[2].matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)].map((decl) => [
          decl[1],
          decl[2].trim(),
        ]),
      ),
    }))
    .filter((block) => block.selector.includes(':root'))
}

function themesFrom(css: string) {
  const blocks = themeBlocks(css)
  const base = blocks.find((block) => block.selector === ':root')
  if (!base) throw new Error('missing :root tokens')
  const themes = [{ name: 'light', values: base.values }]
  for (const block of blocks) {
    const name = /data-theme=(['"])([^'"]+)\1/.exec(block.selector)?.[2]
    if (!name) continue
    const values = new Map(base.values)
    for (const [key, value] of block.values) values.set(key, value)
    themes.push({ name, values })
  }
  return themes
}

function lengthPx(value: string) {
  const term = value.trim()
  if (term.endsWith('rem')) return Number.parseFloat(term) * 16
  if (term.endsWith('px')) return Number.parseFloat(term)
  throw new Error(`unsupported length ${term}`)
}

it('gives the focus ring 3:1 contrast against the surfaces it is drawn on', () => {
  const themes = themesFrom(tokens)
  expect(themes.map((theme) => theme.name)).toEqual(
    expect.arrayContaining(['light', 'dark']),
  )
  for (const theme of themes) {
    const focus = theme.values.get('--color-focus')
    expect(focus, theme.name).toBeTruthy()
    for (const token of adjacent) {
      const surface = theme.values.get(token)
      expect(surface, `${theme.name} ${token}`).toBeTruthy()
      const ratio = contrast(focus ?? '', surface ?? '')
      expect(
        ratio,
        `${theme.name} ${focus} on ${token} ${surface}`,
      ).toBeGreaterThanOrEqual(3)
    }
  }
})

it('paints a keyboard ring and a remote ring from the focus token', () => {
  const rings = [...styles.matchAll(/([^{}]+)\{([^{}]*)\}/g)].filter((match) =>
    match[2].includes('var(--color-focus)'),
  )
  const selectors = rings.flatMap((match) =>
    match[1].split(',').map((selector) => selector.trim()),
  )
  expect(selectors).toContain(':focus')
  expect(selectors).toContain(`.${spatialFocusClass}:focus`)
  for (const ring of rings) {
    const outline = /outline:\s*([^;]+);/.exec(ring[2])
    expect(outline, ring[1]).toBeTruthy()
    const width = outline?.[1].trim().split(/\s+/)[0] ?? ''
    expect(lengthPx(width), ring[1]).toBeGreaterThanOrEqual(2)
    const offset = /outline-offset:\s*([^;]+);/.exec(ring[2])
    expect(offset, ring[1]).toBeTruthy()
    expect(lengthPx(offset?.[1] ?? ''), ring[1]).toBeGreaterThan(0)
  }
})
