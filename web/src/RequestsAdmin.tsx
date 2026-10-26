import { useEffect, useState } from 'react'
import { useT, type MessageKey } from './i18n'
import { listKidProfiles, type KidProfile } from './kidProfiles'
import {
  approveRequests,
  denyRequests,
  grantRequest,
  listRequests,
  maxBatch,
  pageSize,
  previewGrant,
  revokeRequests,
  type DecisionItem,
  type DecisionStatus,
  type GrantPreview,
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

type Part = 'pending' | 'labeled' | 'profiles' | 'stuck'

const partKey: Record<Part, MessageKey> = {
  pending: 'requests.partPending',
  labeled: 'requests.partLabeled',
  profiles: 'requests.partProfiles',
  stuck: 'requests.partStuck',
}

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
  const [labeledTotal, setLabeledTotal] = useState(0)
  const [unfinished, setUnfinished] = useState(0)
  const [unfinishedOpen, setUnfinishedOpen] = useState(false)
  const [failedRows, setFailedRows] = useState<ParentRequest[]>([])
  const [failedTotal, setFailedTotal] = useState(0)
  const [availableRows, setAvailableRows] = useState<ParentRequest[]>([])
  const [availableTotal, setAvailableTotal] = useState(0)
  const [unfinishedBroken, setUnfinishedBroken] = useState(false)
  const [profiles, setProfiles] = useState<KidProfile[]>([])
  const [failedParts, setFailedParts] = useState<Part[]>([])
  const [selected, setSelected] = useState<number[]>([])
  const [reason, setReason] = useState('')
  const [grantProfile, setGrantProfile] = useState('')
  const [grantKind, setGrantKind] = useState<'movie' | 'show'>('movie')
  const [grantTmdb, setGrantTmdb] = useState('')
  const [preview, setPreview] = useState<GrantPreview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [results, setResults] = useState<Failure[]>([])
  const [busy, setBusy] = useState(false)
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let current = true
    void Promise.allSettled([
      listRequests('pending'),
      listRequests('labeled'),
      listKidProfiles(),
    ]).then(([pending, ready, kids]) => {
      if (!current) return
      const failed: Part[] = []
      if (pending.status === 'fulfilled') {
        setRows(pending.value.items)
        setTotal(pending.value.total)
        setUnfinished(pending.value.unfinished)
      } else {
        failed.push('pending')
      }
      if (ready.status === 'fulfilled') {
        setLabeled(ready.value.items)
        setLabeledTotal(ready.value.total)
      } else {
        failed.push('labeled')
      }
      if (kids.status === 'fulfilled') setProfiles(kids.value)
      else failed.push('profiles')
      setFailedParts(failed)
      setSelected([])
    })
    return () => {
      current = false
    }
  }, [version])

  useEffect(() => {
    if (!unfinishedOpen) return
    let current = true
    void Promise.all([listRequests('failed'), listRequests('available')]).then(
      ([failed, available]) => {
        if (!current) return
        setFailedRows(failed.items)
        setFailedTotal(failed.total)
        setAvailableRows(available.items)
        setAvailableTotal(available.total)
        setUnfinishedBroken(false)
      },
      () => {
        if (current) setUnfinishedBroken(true)
      },
    )
    return () => {
      current = false
    }
  }, [version, unfinishedOpen])

  const brokenParts: Part[] = unfinishedBroken
    ? [...failedParts, 'stuck']
    : failedParts
  const unfinishedRows = [
    ...failedRows,
    ...availableRows.filter((row) => row.canRevoke),
  ]
  const moreUnfinished =
    failedRows.length < failedTotal || availableRows.length < availableTotal

  function reload() {
    setVersion((current) => current + 1)
  }

  function clearMessages() {
    setError(null)
    setNotice(null)
    setResults([])
  }

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
    clearMessages()
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
      reload()
    }
  }

  async function loadMoreLabeled() {
    if (busy) return
    clearMessages()
    setBusy(true)
    try {
      const page = await listRequests('labeled', pageSize, labeled.length)
      setLabeled((current) => appendNew(current, page.items))
      setLabeledTotal(page.total)
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    } finally {
      setBusy(false)
    }
  }

  async function loadMoreUnfinished() {
    if (busy) return
    clearMessages()
    setBusy(true)
    try {
      if (failedRows.length < failedTotal) {
        const page = await listRequests('failed', pageSize, failedRows.length)
        setFailedRows((current) => appendNew(current, page.items))
        setFailedTotal(page.total)
      } else {
        const page = await listRequests(
          'available',
          pageSize,
          availableRows.length,
        )
        setAvailableRows((current) => appendNew(current, page.items))
        setAvailableTotal(page.total)
      }
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    } finally {
      setBusy(false)
    }
  }

  async function revoke(row: ParentRequest) {
    if (busy) return
    clearMessages()
    setBusy(true)
    try {
      const body = await revokeRequests([row.id])
      const item = body.results.find((result) => result.id === row.id)
      if (!item || unresolved(item)) setError(t('requests.revokeFailed'))
      else
        setNotice(
          t(
            item.labelInUse
              ? 'requests.revokeInUse'
              : item.labelNotOwned
                ? 'requests.revokeNotOwned'
                : 'requests.revokeDone',
            {
              title: row.title,
            },
          ),
        )
      reload()
    } catch (failure) {
      setError(messageOf(failure, t('requests.revokeFailed')))
    } finally {
      setBusy(false)
    }
  }

  function changeGrantTitle(kind: 'movie' | 'show', tmdb: string) {
    setGrantKind(kind)
    setGrantTmdb(tmdb)
    setPreview(null)
  }

  async function lookUp() {
    if (busy) return
    clearMessages()
    const text = grantTmdb.trim()
    const tmdbId = Number(text)
    if (
      !/^[0-9]+$/.test(text) ||
      !Number.isSafeInteger(tmdbId) ||
      tmdbId <= 0
    ) {
      setError(t('requests.grantInvalidId'))
      return
    }
    setBusy(true)
    try {
      setPreview(await previewGrant(grantKind, tmdbId))
    } catch (failure) {
      setPreview(null)
      setError(messageOf(failure, t('requests.grantFailed')))
    } finally {
      setBusy(false)
    }
  }

  async function grant() {
    if (busy) return
    const profile = profiles.find((item) => String(item.id) === grantProfile)
    if (!profile || !preview || preview.inLibrary === false) {
      setError(t('requests.grantInvalid'))
      return
    }
    clearMessages()
    setBusy(true)
    try {
      const body = await grantRequest(profile.id, preview.kind, preview.tmdbId)
      setNotice(
        t(
          body.labelNotOwned ? 'requests.grantNotOwned' : 'requests.grantDone',
          {
            title: body.title,
            name: profile.name,
          },
        ),
      )
      changeGrantTitle(grantKind, '')
      reload()
    } catch (failure) {
      setError(messageOf(failure, t('requests.grantFailed')))
    } finally {
      setBusy(false)
    }
  }

  return (
    <section aria-labelledby="requests-heading">
      <h2 id="requests-heading">{t('requests.title')}</h2>
      {brokenParts.length > 0 && (
        <div role="alert">
          <p>
            {t('requests.loadFailed', {
              parts: brokenParts.map((part) => t(partKey[part])).join(', '),
            })}
          </p>
          <button type="button" onClick={reload}>
            {t('requests.retry')}
          </button>
        </div>
      )}
      {error && <p role="alert">{error}</p>}
      {notice && <p role="status">{notice}</p>}
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
      {rows === null && !failedParts.includes('pending') && (
        <p role="status">{t('app.loading')}</p>
      )}
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
                  onClick={() => void revoke(row)}
                >
                  {t('requests.revokeTitle', { title: row.title })}
                </button>
              </li>
            ))}
          </ul>
          {labeled.length < labeledTotal && (
            <button
              type="button"
              disabled={busy}
              onClick={() => void loadMoreLabeled()}
            >
              {t('requests.showMore', {
                shown: String(labeled.length),
                total: String(labeledTotal),
              })}
            </button>
          )}
        </>
      )}
      {unfinished > 0 && (
        <>
          <h3>{t('requests.stuckHeading')}</h3>
          <p>{t('requests.stuckHint')}</p>
          {!unfinishedOpen && (
            <button type="button" onClick={() => setUnfinishedOpen(true)}>
              {t('requests.stuckShow', { count: String(unfinished) })}
            </button>
          )}
          {unfinishedOpen && (
            <ul>
              {unfinishedRows.map((row) => (
                <li key={row.id}>
                  <strong>{row.title}</strong>
                  <span>
                    {t('requests.forKid', {
                      kind: t(
                        row.kind === 'movie'
                          ? 'requests.movie'
                          : 'requests.show',
                      ),
                      name: row.kidName,
                    })}
                  </span>
                  <span>{t(stateKey[row.state])}</span>
                  {row.labelMayRemain && (
                    <span>
                      {t(
                        row.canRevoke
                          ? 'requests.stuckLabelRevoke'
                          : 'requests.stuckLabelCheck',
                      )}
                    </span>
                  )}
                  {row.canRevoke && (
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() => void revoke(row)}
                    >
                      {t('requests.revokeTitle', { title: row.title })}
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
          {unfinishedOpen && moreUnfinished && (
            <button
              type="button"
              disabled={busy}
              onClick={() => void loadMoreUnfinished()}
            >
              {t('requests.showMore', {
                shown: String(unfinishedRows.length),
                total: String(Math.max(unfinished, unfinishedRows.length)),
              })}
            </button>
          )}
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
                changeGrantTitle(
                  event.target.value === 'show' ? 'show' : 'movie',
                  grantTmdb,
                )
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
              onChange={(event) =>
                changeGrantTitle(grantKind, event.target.value)
              }
            />
          </label>
          <button type="button" disabled={busy} onClick={() => void lookUp()}>
            {t('requests.grantLookup')}
          </button>
          {preview && (
            <>
              <p>
                {t('requests.grantFound', {
                  title: preview.title,
                  kind: t(
                    preview.kind === 'movie'
                      ? 'requests.movie'
                      : 'requests.show',
                  ),
                })}
              </p>
              <p>
                {t(
                  preview.inLibrary === true
                    ? 'requests.grantInLibrary'
                    : preview.inLibrary === false
                      ? 'requests.grantNotInLibrary'
                      : 'requests.grantLibraryUnknown',
                )}
              </p>
              <button
                type="button"
                disabled={
                  busy || grantProfile === '' || preview.inLibrary === false
                }
                onClick={() => void grant()}
              >
                {t('requests.grantSubmit', { title: preview.title })}
              </button>
            </>
          )}
        </>
      )}
    </section>
  )
}

function appendNew(current: ParentRequest[], items: ParentRequest[]) {
  return [
    ...current,
    ...items.filter((item) => !current.some((row) => row.id === item.id)),
  ]
}

function unresolved(item: DecisionItem) {
  return item.status !== 'succeeded' && item.status !== 'alreadyDecided'
}
