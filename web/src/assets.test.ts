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

const aboutPage = join(web, 'src', 'about', 'About.tsx')
const dogCredit = join(web, 'src', 'about', 'DogCredit.tsx')
const parentOnlyPages = new Set([aboutPage, dogCredit])

const allowedHref =
  /^https:\/\/(?:www\.themoviedb\.org\/|www\.justwatch\.com\/|fanart\.tv\/|www\.doesthedogdie\.com|www\.gnu\.org\/licenses\/gpl-3\.0\.html)$/

it('loads nothing from, and links to, no third-party host outside the parent About page', () => {
  const files = [join(web, 'index.html'), ...sources(join(web, 'src'))].filter(
    (file) => !parentOnlyPages.has(file),
  )
  for (const file of files) {
    expect(readFileSync(file, 'utf8'), file).not.toMatch(
      /(?:src|href)=["']https?:|url\(\s*["']?(?:https?:)?\/\/|@import\s+(?:url\()?["']?https?:|fonts\.(?:googleapis|gstatic)|window\.open|target=["']_blank/,
    )
  }
})

it('limits the parent About page to the credited sites with safe link attributes', () => {
  const text = readFileSync(aboutPage, 'utf8')
  expect(text).not.toMatch(/src=["']https?:/)
  expect(text.match(/<a\s/g)).toHaveLength(1)
  const hrefs = [...text.matchAll(/href="(https?:[^"]+)"/g)].map(
    (match) => match[1],
  )
  expect(hrefs.length).toBeGreaterThan(0)
  for (const href of hrefs) expect(href).toMatch(allowedHref)
  expect(text).toContain('target="_blank" rel="noreferrer noopener"')
})

it('imports the parent About page only from the app routes', () => {
  const importers = sources(join(web, 'src')).filter((file) =>
    /from ['"][./]*(?:about\/)?About['"]/.test(readFileSync(file, 'utf8')),
  )
  expect(importers.map((file) => file.slice(web.length + 1))).toEqual([
    join('src', 'App.tsx'),
  ])
})

it('limits the warning credit to the parent approval queue', () => {
  const text = readFileSync(dogCredit, 'utf8')
  expect(text).not.toMatch(/src=["']https?:/)
  expect(text.match(/<a\s/g)).toHaveLength(1)
  expect(text).toContain('href={dogCreditUrl}')
  expect(text).toContain('target="_blank" rel="noreferrer noopener"')
  expect(text).toContain('https://www.doesthedogdie.com')
  const importers = sources(join(web, 'src')).filter((file) =>
    /from ['"][./]*about\/DogCredit['"]/.test(readFileSync(file, 'utf8')),
  )
  expect(importers.map((file) => file.slice(web.length + 1))).toEqual([
    join('src', 'ParentHome.tsx'),
  ])
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
