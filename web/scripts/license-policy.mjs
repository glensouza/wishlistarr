function tokenize(expression) {
  if (typeof expression !== 'string' || expression.includes('*')) return null
  const text = expression.trim()
  if (text === '') return null
  const tokens = []
  for (const match of text.matchAll(/\(|\)|\b(?:AND|OR|WITH)\b|[^\s()]+/gi)) {
    const raw = match[0]
    tokens.push(/^(?:AND|OR|WITH)$/i.test(raw) ? raw.toUpperCase() : raw)
  }
  return tokens.length > 0 ? tokens : null
}

class Parser {
  constructor(tokens, allowed) {
    this.tokens = tokens
    this.allowed = allowed
    this.index = 0
  }

  peek() {
    return this.tokens[this.index]
  }

  parseOr() {
    let ok = this.parseAnd()
    while (this.peek() === 'OR') {
      this.index += 1
      ok = this.parseAnd() || ok
    }
    return ok
  }

  parseAnd() {
    let ok = this.parseWith()
    while (this.peek() === 'AND') {
      this.index += 1
      ok = this.parseWith() && ok
    }
    return ok
  }

  parseWith() {
    if (this.peek() === '(') {
      this.index += 1
      const ok = this.parseOr()
      if (this.peek() !== ')') throw new Error('paren')
      this.index += 1
      if (this.peek() === 'WITH') throw new Error('with')
      return ok
    }

    const id = this.readId()
    if (this.peek() !== 'WITH') return this.allowed.has(id)
    this.index += 1
    const exception = this.readId()
    return this.allowed.has(`${id} WITH ${exception}`)
  }

  readId() {
    const token = this.peek()
    if (
      !token ||
      token === '(' ||
      token === ')' ||
      token === 'AND' ||
      token === 'OR' ||
      token === 'WITH' ||
      !/^[A-Za-z0-9.+-]+$/.test(token)
    ) {
      throw new Error('license id')
    }
    this.index += 1
    return token
  }
}

export function licenseAllowed(expression, allowed) {
  const tokens = tokenize(expression)
  if (!tokens) return false
  try {
    const parser = new Parser(tokens, new Set(allowed))
    const ok = parser.parseOr()
    return ok && parser.peek() === undefined
  } catch {
    return false
  }
}
