import { useEffect, useState } from 'react'
import { useT, type MessageKey } from './i18n'
import {
  approveRequests,
  denyRequests,
  listRequests,
  maxBatch,
  type DecisionItem,
  type DecisionStatus,
  type ParentRequest,
} from './requests'

const stateKey: Record<ParentRequest['state'], MessageKey> = {
  pending: 'requests.pending',
  approved: 'requests.approved',
  denied: 'requests.denied',
  fulfilling: 'requests.fulfilling',
  available: 'requests.available',
  labeled: 'requests.labeled',
  failed: 'requests.failed',
}

type Outcome = DecisionStatus | 'unsent' | 'unknown'

const statusKey: Record<Outcome, MessageKey> = {
  succeeded: 'requests.result.succeeded',
  alreadyDecided: 'requests.result.alreadyDecided',
  notFound: 'requests.result.notFound',
  failed: 'requests.result.failed',
  conflict: 'requests.result.conflict',
  notProcessed: 'requests.result.notProcessed',
  unsent: 'requests.result.unsent',
  unknown: 'requests.result.unknown',
}

type Failure = { id: number; title: string; status: Outcome }

const messageOf = (failure: unknown, fallback: string) =>
  failure instanceof Error ? failure.message : fallback

const noteOf = (value: string) => {
  const trimmed = value.trim()
  return trimmed === '' ? null : trimmed
}

export default function RequestsAdmin() {
  const t = useT()
  const [rows, setRows] = useState<ParentRequest[] | null>(null)
  const [total, setTotal] = useState(0)
  const [selected, setSelected] = useState<number[]>([])
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [results, setResults] = useState<Failure[]>([])
  const [busy, setBusy] = useState(false)
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let current = true
    listRequests('pending').then(
      (page) => {
        if (!current) return
        setRows(page.items)
        setTotal(page.total)
        setSelected([])
      },
      (failure: unknown) =>
        current && setError(messageOf(failure, t('app.somethingWrong'))),
    )
    return () => {
      current = false
    }
  }, [version, t])

  function toggle(id: number) {
    setSelected((current) =>
      current.includes(id)
        ? current.filter((item) => item !== id)
        : [...current, id],
    )
  }

  function toggleAll() {
    const ids = rows?.map((row) => row.id) ?? []
    setSelected((current) => (current.length === ids.length ? [] : ids))
  }

  async function decide(action: 'approve' | 'deny', ids: number[]) {
    if (ids.length === 0 || busy) return
    setError(null)
    setResults([])
    setBusy(true)
    const send = action === 'approve' ? approveRequests : denyRequests
    const note = noteOf(reason)
    const titles = new Map(rows?.map((row) => [row.id, row.title]))
    const collected: DecisionItem[] = []
    const stranded: { id: number; status: Outcome }[] = []
    try {
      for (let start = 0; start < ids.length; start += maxBatch) {
        let body
        try {
          body = await send(ids.slice(start, start + maxBatch), note)
        } catch (failure) {
          ids
            .slice(start, start + maxBatch)
            .forEach((id) => stranded.push({ id, status: 'unknown' }))
          ids
            .slice(start + maxBatch)
            .forEach((id) => stranded.push({ id, status: 'unsent' }))
          throw failure
        }
        collected.push(...body.results)
      }
      setReason('')
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    } finally {
      setResults(
        [...collected.filter(unresolved), ...stranded].map((item) => ({
          id: item.id,
          title: titles.get(item.id) ?? `#${item.id}`,
          status: item.status,
        })),
      )
      setBusy(false)
      setVersion((current) => current + 1)
    }
  }

  return (
    <section aria-labelledby="requests-heading">
      <h2 id="requests-heading">{t('requests.title')}</h2>
      {error && <p role="alert">{error}</p>}
      {results.length > 0 && (
        <div role="alert">
          <p>{t('requests.notApplied')}</p>
          <ul>
            {results.map((item) => (
              <li key={item.id}>
                {t('requests.itemResult', {
                  title: item.title,
                  result: t(statusKey[item.status]),
                })}
              </li>
            ))}
          </ul>
        </div>
      )}
      {rows === null && <p role="status">{t('app.loading')}</p>}
      {rows !== null && rows.length === 0 && <p>{t('requests.none')}</p>}
      {rows !== null && rows.length > 0 && (
        <>
          {total > rows.length && (
            <p>
              {t('requests.showing', {
                shown: String(rows.length),
                total: String(total),
              })}
            </p>
          )}
          <label>
            <input
              type="checkbox"
              checked={selected.length === rows.length}
              onChange={toggleAll}
            />
            {t('requests.selectAll')}
          </label>
          <ul>
            {rows.map((row) => (
              <li key={row.id}>
                <label>
                  <input
                    type="checkbox"
                    checked={selected.includes(row.id)}
                    onChange={() => toggle(row.id)}
                  />
                  {t('requests.select', { title: row.title })}
                </label>
                <strong>{row.title}</strong>
                <span>
                  {t('requests.forKid', {
                    kind: t(
                      row.kind === 'movie' ? 'requests.movie' : 'requests.show',
                    ),
                    name: row.kidName,
                  })}
                </span>
                <span>{t(stateKey[row.state])}</span>
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => void decide('approve', [row.id])}
                >
                  {t('requests.approveTitle', { title: row.title })}
                </button>
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => void decide('deny', [row.id])}
                >
                  {t('requests.denyTitle', { title: row.title })}
                </button>
              </li>
            ))}
          </ul>
          <label>
            {t('requests.reason')}
            <input
              value={reason}
              placeholder={t('requests.reasonHint')}
              onChange={(event) => setReason(event.target.value)}
            />
          </label>
          <button
            type="button"
            disabled={busy || selected.length === 0}
            onClick={() => void decide('approve', selected)}
          >
            {t('requests.approveSelected')}
          </button>
          <button
            type="button"
            disabled={busy || selected.length === 0}
            onClick={() => void decide('deny', selected)}
          >
            {t('requests.denySelected')}
          </button>
        </>
      )}
    </section>
  )
}

function unresolved(item: DecisionItem) {
  return item.status !== 'succeeded' && item.status !== 'alreadyDecided'
}
