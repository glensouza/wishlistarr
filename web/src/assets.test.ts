import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { expect, it } from 'vitest'

const web = join(__dirname, '..')
const read = (...parts: string[]) => readFileSync(join(web, ...parts), 'utf8')

function sources(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name)
    if (entry.isDirectory()) return sources(path)
    return /\.(css|tsx?)$/.test(entry.name) && !/\.test\./.test(entry.name)
      ? [path]
      : []
  })
}

it('loads nothing from third-party hosts', () => {
  const files = [join(web, 'index.html'), ...sources(join(web, 'src'))]
  for (const file of files) {
    expect(readFileSync(file, 'utf8'), file).not.toMatch(
      /(?:src|href)=["']https?:|url\(\s*["']?(?:https?:)?\/\/|@import\s+(?:url\()?["']?https?:|fonts\.(?:googleapis|gstatic)/,
    )
  }
})

it('bundles its fonts from packages', () => {
  const main = read('src', 'main.tsx')
  expect(main).toContain("'@fontsource-variable/fredoka'")
  expect(main).toContain("'@fontsource-variable/nunito'")
})

it('is set up for narrow screens', () => {
  expect(read('index.html')).toContain('width=device-width')
})

it('defines colors once as tokens and reuses them', () => {
  expect(read('src', 'theme', 'tokens.css')).toContain("[data-theme='dark']")
  expect(read('src', 'styles.css')).not.toMatch(/#[0-9a-fA-F]{3,8}\b/)
})
