import { contentControlError } from './contentControlError'
import TitleControls from './TitleControls'
import { request } from './api'
import { useEffect, useRef, useState } from 'react'
import { useT, type MessageKey } from './i18n'
import {
  listKidProfiles,
  ratingLevels,
  updateKidProfile,
  type KidProfile,
} from './kidProfiles'
import {
  importRules,
  profileRules,
  validRules,
  type ProfileRules,
} from './contentRules'

export default function ContentControls() {
  const t = useT()
  const [open, setOpen] = useState(false)
  const [locked, setLocked] = useState(false)
  return (
    <section
      aria-labelledby="content-controls-heading"
      className="content-controls"
    >
      <h2 id="content-controls-heading">{t('controls.title')}</h2>
      <button
        type="button"
        aria-expanded={open}
        disabled={locked}
        onClick={() => setOpen(!open)}
      >
        {t(open ? 'controls.close' : 'controls.open')}
      </button>
      {open && <Profiles locked={locked} onLocked={setLocked} />}
    </section>
  )
}

function Profiles({
  locked,
  onLocked,
}: {
  locked: boolean
  onLocked: (locked: boolean) => void
}) {
  const t = useT()
  const [profiles, setProfiles] = useState<KidProfile[] | null>(null)
  const [selected, setSelected] = useState('')
  const [failed, setFailed] = useState(false)
  const [attempt, setAttempt] = useState(0)
  useEffect(() => {
    let current = true
    listKidProfiles().then(
      (rows) => {
        if (current) setProfiles(rows)
      },
      () => {
        if (current) setFailed(true)
      },
    )
    return () => {
      current = false
    }
  }, [attempt])
  const profile = profiles?.find((item) => String(item.id) === selected)
  return (
    <>
      {failed && (
        <p role="alert">
          {t('controls.loadFailed')}{' '}
          <button
            type="button"
            onClick={() => {
              setFailed(false)
              setAttempt(attempt + 1)
            }}
          >
            {t('controls.retry')}
          </button>
        </p>
      )}
      {!profiles && !failed && <p role="status">{t('app.loading')}</p>}
      {profiles && (
        <label>
          {t('controls.profile')}
          <select
            disabled={locked}
            value={selected}
            onChange={(event) => setSelected(event.target.value)}
          >
            <option value="">{t('controls.choose')}</option>
            {profiles.map((item) => (
              <option key={item.id} value={item.id}>
                {item.name}
              </option>
            ))}
          </select>
        </label>
      )}
      {profiles?.length === 0 && <p>{t('admin.none')}</p>}
      {profile && (
        <RulesEditor
          key={profile.id}
          profile={profile}
          onLocked={onLocked}
          onSaved={(saved) =>
            setProfiles((rows) =>
              rows!.map((row) => (row.id === saved.id ? saved : row)),
            )
          }
        />
      )}
    </>
  )
}

const numberFields = [
  ['movieAgeLimit', 'admin.movieLimit', 21],
  ['tvAgeLimit', 'admin.tvLimit', 21],
  ['weeklyAllowance', 'admin.weekly', 1000],
] as const
const listFields = [
  ['blockedGenreIds', 'admin.blockedGenres', 'admin.blockedGenresHint'],
  ['blockedKeywordIds', 'admin.blockedKeywords', 'admin.blockedKeywordsHint'],
  [
    'allowedOriginalLanguages',
    'admin.allowedLanguages',
    'admin.allowedLanguagesHint',
  ],
] as const
const ceilings = [
  ['autoApproveMovieRating', 'admin.movieCeiling'],
  ['autoApproveTvRating', 'admin.tvCeiling'],
] as const
type Draft = Record<
  (typeof numberFields)[number][0] | (typeof listFields)[number][0],
  string
> &
  Pick<
    ProfileRules,
    'autoApprove' | 'autoApproveMovieRating' | 'autoApproveTvRating'
  >
function toDraft(rules: ProfileRules): Draft {
  return {
    ...profileRules(rules),
    movieAgeLimit: rules.movieAgeLimit?.toString() ?? '',
    tvAgeLimit: rules.tvAgeLimit?.toString() ?? '',
    weeklyAllowance: rules.weeklyAllowance?.toString() ?? '',
    blockedGenreIds: rules.blockedGenreIds.join(', '),
    blockedKeywordIds: rules.blockedKeywordIds.join(', '),
    allowedOriginalLanguages: rules.allowedOriginalLanguages.join(', '),
  }
}
function parseDraft(draft: Draft): ProfileRules | null {
  const tokens = (value: string) =>
    value.trim() === '' ? [] : value.split(',').map((part) => part.trim())
  const rules = {
    ...draft,
    movieAgeLimit:
      draft.movieAgeLimit === '' ? null : Number(draft.movieAgeLimit),
    tvAgeLimit: draft.tvAgeLimit === '' ? null : Number(draft.tvAgeLimit),
    weeklyAllowance:
      draft.weeklyAllowance === '' ? null : Number(draft.weeklyAllowance),
    blockedGenreIds: tokens(draft.blockedGenreIds).map(Number),
    blockedKeywordIds: tokens(draft.blockedKeywordIds).map(Number),
    allowedOriginalLanguages: tokens(draft.allowedOriginalLanguages).map(
      (code) => code.toLowerCase(),
    ),
  }
  return validRules(rules) ? profileRules(rules) : null
}

function RulesEditor({
  profile,
  onSaved,
  onLocked,
}: {
  profile: KidProfile
  onSaved: (profile: KidProfile) => void
  onLocked: (locked: boolean) => void
}) {
  const t = useT()
  const [draft, setDraft] = useState(() => toDraft(profile))
  const [state, setState] = useState<
    'idle' | 'pending' | 'saving' | 'saved' | 'failed' | 'importing'
  >('idle')
  const [error, setError] = useState<string | null>(null)
  const [retry, setRetry] = useState(0)
  const [titleVersion, setTitleVersion] = useState(0)
  const saved = useRef(profile)
  const mounted = useRef(true)
  const saveRef = useRef(onSaved)
  useEffect(() => {
    saveRef.current = onSaved
  }, [onSaved])
  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
    }
  }, [])
  const parsed = parseDraft(draft)
  const valid = parsed !== null
  useEffect(() => {
    const rules = parseDraft(draft)
    if (
      !rules ||
      JSON.stringify(rules) === JSON.stringify(profileRules(saved.current))
    )
      return
    const timer = setTimeout(() => {
      setState('saving')
      setError(null)
      const current = saved.current
      void updateKidProfile(current.id, {
        name: current.name,
        avatar: current.avatar,
        plexLabel: current.plexLabel,
        plexUserId: current.plexUserId,
        homeRows: current.homeRows,
        ...rules,
      }).then(
        (result) => {
          saved.current = result
          if (mounted.current) {
            saveRef.current(result)
            setState('saved')
            onLocked(false)
          }
        },
        () => {
          if (mounted.current) {
            setState('failed')
            setError(t('controls.saveFailed'))
          }
        },
      )
    }, 400)
    return () => clearTimeout(timer)
  }, [draft, retry, t, onLocked])

  function change(next: Draft) {
    setDraft(next)
    setError(null)
    const unchanged =
      JSON.stringify(parseDraft(next)) ===
      JSON.stringify(profileRules(saved.current))
    setState(unchanged ? 'idle' : 'pending')
    onLocked(!unchanged)
  }
  const busy =
    state === 'saving' || state === 'pending' || state === 'importing'
  async function upload(file: File | undefined) {
    if (!file) return
    if (file.size > 1048576) {
      setError(t('controls.importInvalid'))
      return
    }
    const previous = state
    setState('importing')
    onLocked(true)
    const invalid = () => {
      setState(previous)
      onLocked(previous !== 'idle' && previous !== 'saved')
      setError(t('controls.importInvalid'))
    }
    try {
      const rules = importRules(await file.text())
      if (!mounted.current) return
      if (!rules) {
        invalid()
        return
      }
      const result = await request<KidProfile>(
        'PUT',
        `/api/v1/kid-profiles/${profile.id}/content-rules`,
        JSON.parse(await file.text()),
      )
      saved.current = result
      saveRef.current(result)
      setDraft(toDraft(result))
      setState('saved')
      onLocked(false)
      setTitleVersion((value) => value + 1)
    } catch (error) {
      if (mounted.current) {
        invalid()
        setError(t(contentControlError(error, true)))
      }
    }
  }
  async function download() {
    try {
      const file = await request<unknown>(
        'GET',
        `/api/v1/kid-profiles/${profile.id}/content-rules`,
      )
      const url = URL.createObjectURL(
        new Blob([JSON.stringify(file, null, 2)], { type: 'application/json' }),
      )
      const link = document.createElement('a')
      link.href = url
      link.download = `content-rules-${profile.id}.json`
      link.click()
      URL.revokeObjectURL(url)
    } catch {
      setError(t('controls.loadFailed'))
    }
  }
  return (
    <div>
      <p>{t('controls.hint')}</p>
      <TitleControls
        key={titleVersion}
        profileId={profile.id}
        disabled={busy}
      />
      <fieldset disabled={state === 'saving' || state === 'importing'}>
        <legend>{t('controls.rules', { name: profile.name })}</legend>
        {numberFields.map(([key, label, max]) => (
          <label key={key}>
            {t(label)}
            <input
              type="number"
              min={0}
              max={max}
              step={1}
              value={draft[key]}
              onChange={(event) =>
                change({ ...draft, [key]: event.target.value })
              }
            />
          </label>
        ))}
        <p className="muted">{t('controls.empty')}</p>
        {listFields.map(([key, label, hint]) => (
          <label key={key}>
            {t(label)}
            <input
              value={draft[key]}
              aria-describedby={`${key}-hint`}
              onChange={(event) =>
                change({ ...draft, [key]: event.target.value })
              }
            />
            <span className="muted" id={`${key}-hint`}>
              {t(hint)}
            </span>
          </label>
        ))}
        <label>
          <input
            type="checkbox"
            checked={draft.autoApprove}
            onChange={(event) =>
              change({ ...draft, autoApprove: event.target.checked })
            }
          />
          {t('admin.autoApprove')}
        </label>
        <p className="muted">{t('admin.autoApproveHint')}</p>
        {ceilings.map(([key, label]) => (
          <label key={key}>
            {t(label)}
            <select
              value={draft[key] ?? ''}
              onChange={(event) =>
                change({ ...draft, [key]: event.target.value || null } as Draft)
              }
            >
              <option value="">{t('admin.ratingNone')}</option>
              {ratingLevels.map((rating) => (
                <option key={rating} value={rating}>
                  {t(`rating.${rating}` as MessageKey)}
                </option>
              ))}
            </select>
          </label>
        ))}
      </fieldset>
      {!valid && <p role="alert">{t('controls.invalid')}</p>}
      {valid && (
        <p role="status">
          {t(
            state === 'importing'
              ? 'controls.importing'
              : state === 'pending' || state === 'saving'
                ? 'controls.saving'
                : state === 'saved'
                  ? 'controls.saved'
                  : state === 'failed'
                    ? 'controls.unsaved'
                    : 'controls.ready',
          )}
        </p>
      )}
      {error && <p role="alert">{error}</p>}
      {state === 'failed' && (
        <button
          type="button"
          onClick={() => {
            setState('pending')
            setRetry(retry + 1)
          }}
        >
          {t('controls.retry')}
        </button>
      )}
      {state !== 'idle' && state !== 'saved' && (
        <button
          type="button"
          disabled={state === 'saving' || state === 'importing'}
          onClick={() => {
            setDraft(toDraft(saved.current))
            setState('idle')
            setError(null)
            onLocked(false)
          }}
        >
          {t('controls.discard')}
        </button>
      )}
      <button
        type="button"
        disabled={busy || !valid || state === 'failed'}
        onClick={() => void download()}
      >
        {t('controls.export')}
      </button>
      <label>
        {t('controls.import')}
        <input
          type="file"
          accept="application/json,.json"
          disabled={busy}
          onChange={(event) => {
            void upload(event.target.files?.[0])
            event.target.value = ''
          }}
        />
      </label>
    </div>
  )
}
