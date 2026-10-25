import { useEffect, useState } from 'react'
import { useT, type MessageKey } from './i18n'
import { listKidProfiles, type KidProfile } from './kidProfiles'
import {
  approveRequests,
  denyRequests,
  grantRequest,
  listRequests,
  maxBatch,
  revokeRequests,
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
  revoked: 'requests.revoked',
}

const statusKey: Record<DecisionStatus, MessageKey> = {
  succeeded: 'requests.result.succeeded',
  alreadyDecided: 'requests.result.alreadyDecided',
  notFound: 'requests.result.notFound',
  failed: 'requests.result.failed',
  conflict: 'requests.result.conflict',
}

type Failure = { id: number; title: string; status: DecisionStatus }

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
  const [labeled, setLabeled] = useState<ParentRequest[]>([])
  const [profiles, setProfiles] = useState<KidProfile[]>([])
  const [selected, setSelected] = useState<number[]>([])
  const [reason, setReason] = useState('')
  const [grantProfile, setGrantProfile] = useState('')
  const [grantKind, setGrantKind] = useState<'movie' | 'show'>('movie')
  const [grantTmdb, setGrantTmdb] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [results, setResults] = useState<Failure[]>([])
  const [busy, setBusy] = useState(false)
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let current = true
    Promise.all([
      listRequests('pending'),
      listRequests('labeled'),
      listKidProfiles(),
    ]).then(
      ([pending, ready, kids]) => {
        if (!current) return
        setRows(pending.items)
        setTotal(pending.total)
        setLabeled(ready.items)
        setProfiles(kids)
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
    try {
      for (let start = 0; start < ids.length; start += maxBatch) {
        const body = await send(ids.slice(start, start + maxBatch), note)
        collected.push(...body.results)
      }
      setReason('')
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    } finally {
      setResults(
        collected.filter(unresolved).map((item) => ({
          id: item.id,
          title: titles.get(item.id) ?? `#${item.id}`,
          status: item.status,
        })),
      )
      setBusy(false)
      setVersion((current) => current + 1)
    }
  }

  async function revoke(id: number) {
    if (busy) return
    setError(null)
    setBusy(true)
    try {
      const body = await revokeRequests([id])
      if (body.results.some(unresolved)) setError(t('requests.revokeFailed'))
      setVersion((current) => current + 1)
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    } finally {
      setBusy(false)
    }
  }

  async function grant() {
    const tmdbId = Number(grantTmdb)
    if (
      busy ||
      grantProfile === '' ||
      !Number.isInteger(tmdbId) ||
      tmdbId <= 0
    ) {
      setError(t('requests.grantInvalid'))
      return
    }
    setError(null)
    setBusy(true)
    try {
      await grantRequest(Number(grantProfile), grantKind, tmdbId)
      setGrantTmdb('')
      setVersion((current) => current + 1)
    } catch (failure) {
      setError(messageOf(failure, t('requests.grantFailed')))
    } finally {
      setBusy(false)
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
      {labeled.length > 0 && (
        <>
          <h3>{t('requests.labeledHeading')}</h3>
          <ul>
            {labeled.map((row) => (
              <li key={row.id}>
                <strong>{row.title}</strong>
                <span>
                  {t('requests.forKid', {
                    kind: t(
                      row.kind === 'movie' ? 'requests.movie' : 'requests.show',
                    ),
                    name: row.kidName,
                  })}
                </span>
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => void revoke(row.id)}
                >
                  {t('requests.revokeTitle', { title: row.title })}
                </button>
              </li>
            ))}
          </ul>
        </>
      )}
      {profiles.length > 0 && (
        <>
          <h3>{t('requests.grantHeading')}</h3>
          <label>
            {t('requests.grantKid')}
            <select
              value={grantProfile}
              onChange={(event) => setGrantProfile(event.target.value)}
            >
              <option value="">{t('requests.grantPickKid')}</option>
              {profiles.map((profile) => (
                <option key={profile.id} value={profile.id}>
                  {profile.name}
                </option>
              ))}
            </select>
          </label>
          <label>
            {t('requests.grantKind')}
            <select
              value={grantKind}
              onChange={(event) =>
                setGrantKind(event.target.value === 'show' ? 'show' : 'movie')
              }
            >
              <option value="movie">{t('requests.movie')}</option>
              <option value="show">{t('requests.show')}</option>
            </select>
          </label>
          <label>
            {t('requests.grantTmdb')}
            <input
              inputMode="numeric"
              value={grantTmdb}
              onChange={(event) => setGrantTmdb(event.target.value)}
            />
          </label>
          <button type="button" disabled={busy} onClick={() => void grant()}>
            {t('requests.grantSubmit')}
          </button>
        </>
      )}
    </section>
  )
}

function unresolved(item: DecisionItem) {
  return item.status !== 'succeeded' && item.status !== 'alreadyDecided'
}
