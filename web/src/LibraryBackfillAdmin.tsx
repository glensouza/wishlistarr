import { useEffect, useRef, useState } from 'react'
import { useT } from './i18n'
import { listKidProfiles, type KidProfile } from './kidProfiles'
import {
  applyBackfill,
  changeBackfill,
  getBackfill,
  listBackfills,
  previewBackfill,
  type BackfillBatch,
  type BackfillDetail,
  type BackfillPreview,
  type BackfillPreviewItem,
} from './libraryBackfill'

const active = (batch: BackfillBatch) =>
  batch.state === 'applying' || batch.state === 'undoing'

const titleKey = (item: BackfillPreviewItem) =>
  item.tmdbId === null
    ? `item:${item.libraryItemId}`
    : `${item.kind}:${item.tmdbId}`

export default function LibraryBackfillAdmin() {
  const t = useT()
  const [open, setOpen] = useState(false)
  const [profiles, setProfiles] = useState<KidProfile[]>([])
  const [profileId, setProfileId] = useState<number | null>(null)
  const [offset, setOffset] = useState(0)
  const [preview, setPreview] = useState<BackfillPreview | null>(null)
  const [selection, setSelection] = useState<Map<number, string>>(new Map())
  const seen = useRef(
    new Map<
      number,
      { title: string; outcome: BackfillPreviewItem['outcome'] }
    >(),
  )
  const [batches, setBatches] = useState<BackfillBatch[]>([])
  const [detail, setDetail] = useState<BackfillDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [working, setWorking] = useState(false)
  const [loading, setLoading] = useState(false)

  const message = (failure: unknown) =>
    failure instanceof Error ? failure.message : t('backfill.failed')

  useEffect(() => {
    if (!open) return
    let current = true
    Promise.all([listKidProfiles(), listBackfills()]).then(
      ([loadedProfiles, loadedBatches]) => {
        if (!current) return
        setProfiles(loadedProfiles)
        setBatches(loadedBatches)
      },
      (failure: unknown) => {
        if (current)
          setError(
            failure instanceof Error ? failure.message : t('backfill.failed'),
          )
      },
    )
    return () => {
      current = false
    }
  }, [open, t])

  useEffect(() => {
    if (!open || profileId === null) return
    let current = true
    previewBackfill(profileId, offset).then(
      (loaded) => {
        if (!current) return
        setPreview(loaded)
        const unseen = loaded.items.filter(
          (item) => !seen.current.has(item.libraryItemId),
        )
        const decisions = new Map(
          loaded.items.map((item) => [titleKey(item), item]),
        )
        const newlyRestricted = new Set<number>()
        for (const [id, previous] of seen.current) {
          const latest = decisions.get(previous.title)
          if (!latest) continue
          if (
            !latest.selectable ||
            (latest.outcome !== previous.outcome &&
              latest.outcome !== 'allowed')
          )
            newlyRestricted.add(id)
          seen.current.set(id, {
            title: previous.title,
            outcome: latest.outcome,
          })
        }
        for (const item of loaded.items)
          seen.current.set(item.libraryItemId, {
            title: titleKey(item),
            outcome: item.outcome,
          })
        setSelection((previous) => {
          const next = new Map(previous)
          for (const id of newlyRestricted) next.delete(id)
          for (const item of loaded.items) {
            if (
              !item.selectable ||
              (next.has(item.libraryItemId) &&
                next.get(item.libraryItemId) !== item.guid)
            )
              next.delete(item.libraryItemId)
          }
          for (const item of unseen) {
            if (item.selectable && item.outcome === 'allowed')
              next.set(item.libraryItemId, item.guid)
          }
          return next
        })
        setLoading(false)
      },
      (failure: unknown) => {
        if (!current) return
        setLoading(false)
        setError(
          failure instanceof Error ? failure.message : t('backfill.failed'),
        )
      },
    )
    return () => {
      current = false
    }
  }, [open, profileId, offset, t])

  const running = batches.some(active)
  useEffect(() => {
    if (!open || !running) return
    let current = true
    const timer = setInterval(() => {
      listBackfills().then(
        (loaded) => current && setBatches(loaded),
        (failure: unknown) =>
          current &&
          setError(
            failure instanceof Error ? failure.message : t('backfill.failed'),
          ),
      )
    }, 2000)
    return () => {
      current = false
      clearInterval(timer)
    }
  }, [open, running, t])

  async function apply(dryRun: boolean) {
    if (!open || loading || working || !preview || selection.size === 0) return
    setError(null)
    setNotice(null)
    setWorking(true)
    try {
      await applyBackfill(
        preview,
        [...selection].map(([libraryItemId, guid]) => ({
          libraryItemId,
          guid,
        })),
        dryRun,
      )
      setNotice(t(dryRun ? 'backfill.dryDone' : 'backfill.queued'))
      if (!dryRun) {
        setBatches(await listBackfills())
        setSelection(new Map())
      }
    } catch (failure) {
      setError(message(failure))
    } finally {
      setWorking(false)
    }
  }

  async function change(id: number, action: 'resume' | 'undo') {
    setError(null)
    setWorking(true)
    try {
      await changeBackfill(id, action)
      setBatches(await listBackfills())
      setDetail(null)
    } catch (failure) {
      setError(message(failure))
    } finally {
      setWorking(false)
    }
  }

  async function inspect(id: number) {
    try {
      setDetail(await getBackfill(id))
    } catch (failure) {
      setError(message(failure))
    }
  }

  function toggle(item: BackfillPreviewItem) {
    setSelection((previous) => {
      const next = new Map(previous)
      if (next.has(item.libraryItemId)) next.delete(item.libraryItemId)
      else next.set(item.libraryItemId, item.guid)
      return next
    })
  }

  function page(next: number) {
    setOffset(next)
    setPreview(null)
    setError(null)
    setLoading(true)
  }

  function reason(item: BackfillPreviewItem) {
    const code = item.reason.code
    switch (code) {
      case 'withinRatingLimit':
      case 'noRatingLimit':
      case 'explicitlyAllowed':
      case 'noBlockedGenre':
      case 'noBlockedKeyword':
      case 'noLanguageLimit':
      case 'languageAllowed':
        return t('backfill.reasonAllowed')
      case 'aboveRatingLimit':
        return t('backfill.reasonRating', { rating: item.reason.rating ?? '' })
      case 'unrated':
        return t('backfill.reasonUnrated')
      case 'unknownGenres':
        return t('backfill.reasonUnknownGenres')
      case 'previouslyRevoked':
        return t('backfill.reasonPreviouslyRevoked')
      case 'explicitlyBlocked':
        return t('backfill.reasonBlocked')
      case 'blockedGenre':
        return t('backfill.reasonGenre')
      case 'blockedKeyword':
        return t('backfill.reasonKeyword')
      case 'languageNotAllowed':
        return t('backfill.reasonLanguage')
      default:
        return t('backfill.reasonUnavailable')
    }
  }

  return (
    <section aria-labelledby="backfill-heading">
      <h2 id="backfill-heading">{t('backfill.title')}</h2>
      <button
        type="button"
        aria-expanded={open}
        onClick={() => {
          setOpen(!open)
          setPreview(null)
          setError(null)
          setNotice(null)
          if (!open && profileId !== null) setLoading(true)
        }}
      >
        {t(open ? 'backfill.close' : 'backfill.open')}
      </button>
      {open && (
        <>
          <p>{t('backfill.hint')}</p>
          {error && <p role="alert">{error}</p>}
          {notice && <p role="status">{notice}</p>}
          <label>
            {t('backfill.profile')}
            <select
              value={profileId ?? ''}
              disabled={working}
              onChange={(event) => {
                setProfileId(
                  event.target.value ? Number(event.target.value) : null,
                )
                setOffset(0)
                setPreview(null)
                setSelection(new Map())
                seen.current.clear()
                setNotice(null)
                setError(null)
                setLoading(event.target.value !== '')
              }}
            >
              <option value="">{t('backfill.choose')}</option>
              {profiles.map((profile) => (
                <option key={profile.id} value={profile.id}>
                  {profile.name}
                </option>
              ))}
            </select>
          </label>
          {loading && <p role="status">{t('backfill.loading')}</p>}
          {preview && (
            <>
              <p>{t('backfill.selection', { count: selection.size })}</p>
              <p>{t('backfill.override')}</p>
              {(['allowed', 'needsApproval', 'blocked'] as const).map(
                (outcome) => (
                  <fieldset key={outcome} disabled={working}>
                    <legend>{t(`backfill.${outcome}`)}</legend>
                    {preview.items
                      .filter((item) => item.outcome === outcome)
                      .map((item) => (
                        <label
                          className="backfill-item"
                          key={item.libraryItemId}
                        >
                          <input
                            type="checkbox"
                            checked={selection.has(item.libraryItemId)}
                            disabled={!item.selectable}
                            onChange={() => toggle(item)}
                          />
                          <span>
                            {item.title} — {reason(item)}
                          </span>
                        </label>
                      ))}
                  </fieldset>
                ),
              )}
              <button
                type="button"
                disabled={working || offset === 0}
                onClick={() => page(Math.max(0, offset - preview.limit))}
              >
                {t('backfill.previous')}
              </button>
              <button
                type="button"
                disabled={working || offset + preview.limit >= preview.total}
                onClick={() => page(offset + preview.limit)}
              >
                {t('backfill.next')}
              </button>
              <button
                type="button"
                disabled={working || loading || selection.size === 0}
                onClick={() => void apply(true)}
              >
                {t('backfill.dryRun')}
              </button>
              <button
                type="button"
                disabled={working || loading || selection.size === 0}
                onClick={() => void apply(false)}
              >
                {t('backfill.apply')}
              </button>
            </>
          )}
          <h3>{t('backfill.history')}</h3>
          <ul>
            {batches.map((batch) => (
              <li key={batch.id}>
                <p role="status">
                  {t('backfill.progress', {
                    id: batch.id,
                    label: batch.label,
                    state: t(`backfill.state.${batch.state}`),
                    total: batch.total,
                    applied: batch.applied,
                    preserved: batch.preserved,
                    skipped: batch.skipped,
                    undone: batch.undone,
                  })}
                </p>
                {batch.error && <p role="alert">{batch.error}</p>}
                <button type="button" onClick={() => void inspect(batch.id)}>
                  {t('backfill.details', { id: batch.id })}
                </button>
                {(batch.state === 'paused' || batch.state === 'undoPaused') && (
                  <button
                    type="button"
                    disabled={working}
                    onClick={() => void change(batch.id, 'resume')}
                  >
                    {t('backfill.resume', { id: batch.id })}
                  </button>
                )}
                {batch.state !== 'undone' &&
                  batch.state !== 'undoing' &&
                  batch.state !== 'undoPaused' && (
                    <button
                      type="button"
                      disabled={working}
                      onClick={() => void change(batch.id, 'undo')}
                    >
                      {t('backfill.undo', { id: batch.id })}
                    </button>
                  )}
              </li>
            ))}
          </ul>
          {detail && (
            <ul aria-label={t('backfill.results')}>
              {detail.items.map((item, index) => (
                <li key={index}>
                  {item.title}:{' '}
                  {t(`backfill.item.${item.state}` as Parameters<typeof t>[0])}
                  {item.detail && ` — ${item.detail}`}
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </section>
  )
}
