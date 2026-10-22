import { useEffect, useState, type FormEvent } from 'react'
import {
  avatars,
  createKidProfile,
  deleteKidProfile,
  listKidProfiles,
  unlockKidProfile,
  updateKidProfile,
  type KidProfile,
  type KidProfileInput,
} from './kidProfiles'
import { useT } from './i18n'
import PlexUserPicker from './PlexUserPicker'

type FormState = {
  name: string
  avatar: string
  pin: string
  removePin: boolean
  movieAgeLimit: string
  tvAgeLimit: string
  plexLabel: string
  plexUserId: string
  weeklyAllowance: string
  blockedGenres: string
  blockedKeywords: string
  allowedLanguages: string
}

const emptyForm: FormState = {
  name: '',
  avatar: avatars[0],
  pin: '',
  removePin: false,
  movieAgeLimit: '',
  tvAgeLimit: '',
  plexLabel: '',
  plexUserId: '',
  weeklyAllowance: '',
  blockedGenres: '',
  blockedKeywords: '',
  allowedLanguages: '',
}

function toForm(profile: KidProfile): FormState {
  return {
    name: profile.name,
    avatar: profile.avatar,
    pin: '',
    removePin: false,
    movieAgeLimit: profile.movieAgeLimit?.toString() ?? '',
    tvAgeLimit: profile.tvAgeLimit?.toString() ?? '',
    plexLabel: profile.plexLabel,
    plexUserId: profile.plexUserId ?? '',
    weeklyAllowance: profile.weeklyAllowance?.toString() ?? '',
    blockedGenres: profile.blockedGenreIds.join(', '),
    blockedKeywords: profile.blockedKeywordIds.join(', '),
    allowedLanguages: profile.allowedOriginalLanguages.join(', '),
  }
}

function parseTokens(value: string): string[] {
  return value
    .split(',')
    .map((part) => part.trim())
    .filter((part) => part !== '')
}

function parseIds(value: string): number[] | null {
  const ids: number[] = []
  for (const token of parseTokens(value)) {
    if (!/^[1-9]\d*$/.test(token)) return null
    ids.push(Number(token))
  }
  return ids
}

const toNumber = (value: string) => (value.trim() === '' ? null : Number(value))

function toInput(form: FormState): KidProfileInput | null {
  const blockedGenreIds = parseIds(form.blockedGenres)
  const blockedKeywordIds = parseIds(form.blockedKeywords)
  if (blockedGenreIds === null || blockedKeywordIds === null) return null
  return {
    name: form.name,
    avatar: form.avatar,
    pin: form.pin === '' ? undefined : form.pin,
    removePin: form.removePin,
    movieAgeLimit: toNumber(form.movieAgeLimit),
    tvAgeLimit: toNumber(form.tvAgeLimit),
    plexLabel: form.plexLabel,
    plexUserId: form.plexUserId === '' ? null : form.plexUserId,
    weeklyAllowance: toNumber(form.weeklyAllowance),
    blockedGenreIds,
    blockedKeywordIds,
    allowedOriginalLanguages: parseTokens(form.allowedLanguages),
  }
}

const messageOf = (failure: unknown, fallback: string) =>
  failure instanceof Error ? failure.message : fallback

export default function KidProfilesAdmin() {
  const t = useT()
  const [profiles, setProfiles] = useState<KidProfile[]>([])
  const [editing, setEditing] = useState<KidProfile | 'new' | null>(null)
  const [deleting, setDeleting] = useState<KidProfile | null>(null)
  const [error, setError] = useState<string | null>(null)

  const [version, setVersion] = useState(0)
  const load = () => setVersion((current) => current + 1)

  useEffect(() => {
    let current = true
    listKidProfiles().then(
      (loaded) => current && setProfiles(loaded),
      (failure: unknown) =>
        current && setError(messageOf(failure, t('app.somethingWrong'))),
    )
    return () => {
      current = false
    }
  }, [version, t])

  async function unlock(profile: KidProfile) {
    setError(null)
    try {
      await unlockKidProfile(profile.id)
      load()
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    }
  }

  return (
    <section aria-labelledby="kid-profiles-heading">
      <h2 id="kid-profiles-heading">{t('admin.title')}</h2>
      {error && !editing && <p role="alert">{error}</p>}
      <ul>
        {profiles.map((profile) => (
          <li key={profile.id}>
            <strong>{profile.name}</strong> ({profile.avatar})
            {profile.isLocked && <span> {t('admin.locked')}</span>}
            <button type="button" onClick={() => setEditing(profile)}>
              {t('admin.edit', { name: profile.name })}
            </button>
            {profile.isLocked && (
              <button type="button" onClick={() => void unlock(profile)}>
                {t('admin.unlock', { name: profile.name })}
              </button>
            )}
            <button type="button" onClick={() => setDeleting(profile)}>
              {t('admin.delete', { name: profile.name })}
            </button>
          </li>
        ))}
      </ul>
      {profiles.length === 0 && <p>{t('admin.none')}</p>}
      {editing === null && (
        <button type="button" onClick={() => setEditing('new')}>
          {t('admin.add')}
        </button>
      )}
      {editing !== null && (
        <ProfileForm
          key={editing === 'new' ? 'new' : editing.id}
          profile={editing === 'new' ? null : editing}
          onCancel={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            load()
          }}
        />
      )}
      {deleting && (
        <DeleteConfirm
          profile={deleting}
          onCancel={() => setDeleting(null)}
          onDeleted={() => {
            setDeleting(null)
            load()
          }}
        />
      )}
    </section>
  )
}

type ProfileFormProps = {
  profile: KidProfile | null
  onCancel: () => void
  onSaved: () => void
}

function ProfileForm({ profile, onCancel, onSaved }: ProfileFormProps) {
  const t = useT()
  const [form, setForm] = useState<FormState>(
    profile ? toForm(profile) : emptyForm,
  )
  const [error, setError] = useState<string | null>(null)

  const set = <K extends keyof FormState>(key: K, value: FormState[K]) =>
    setForm((current) => ({ ...current, [key]: value }))

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    try {
      const input = toInput(form)
      if (input === null) {
        setError(t('admin.rulesInvalid'))
        return
      }
      if (profile) {
        await updateKidProfile(profile.id, input)
      } else {
        await createKidProfile(input)
      }
      onSaved()
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    }
  }

  return (
    <form
      onSubmit={submit}
      aria-label={profile ? t('admin.editForm') : t('admin.newForm')}
    >
      <label>
        {t('admin.name')}
        <input
          value={form.name}
          onChange={(event) => set('name', event.target.value)}
        />
      </label>
      <label>
        {t('admin.avatar')}
        <select
          value={form.avatar}
          onChange={(event) => set('avatar', event.target.value)}
        >
          {avatars.map((avatar) => (
            <option key={avatar} value={avatar}>
              {avatar}
            </option>
          ))}
        </select>
      </label>
      <label>
        {t('admin.pin')}
        <input
          type="password"
          inputMode="numeric"
          autoComplete="off"
          value={form.pin}
          placeholder={
            profile?.hasPin ? t('admin.pinUnchanged') : t('admin.pinHint')
          }
          onChange={(event) => set('pin', event.target.value)}
        />
      </label>
      {profile?.hasPin && (
        <label>
          <input
            type="checkbox"
            checked={form.removePin}
            onChange={(event) => set('removePin', event.target.checked)}
          />
          {t('admin.removePin')}
        </label>
      )}
      <label>
        {t('admin.movieLimit')}
        <input
          type="number"
          min={0}
          max={21}
          value={form.movieAgeLimit}
          onChange={(event) => set('movieAgeLimit', event.target.value)}
        />
      </label>
      <label>
        {t('admin.tvLimit')}
        <input
          type="number"
          min={0}
          max={21}
          value={form.tvAgeLimit}
          onChange={(event) => set('tvAgeLimit', event.target.value)}
        />
      </label>
      <label>
        {t('admin.plexLabel')}
        <input
          value={form.plexLabel}
          placeholder={form.name}
          onChange={(event) => set('plexLabel', event.target.value)}
        />
      </label>
      <PlexUserPicker
        value={form.plexUserId}
        onChange={(value) => set('plexUserId', value)}
      />
      <label>
        {t('admin.blockedGenres')}
        <input
          value={form.blockedGenres}
          placeholder={t('admin.blockedGenresHint')}
          onChange={(event) => set('blockedGenres', event.target.value)}
        />
      </label>
      <label>
        {t('admin.blockedKeywords')}
        <input
          value={form.blockedKeywords}
          placeholder={t('admin.blockedKeywordsHint')}
          onChange={(event) => set('blockedKeywords', event.target.value)}
        />
      </label>
      <label>
        {t('admin.allowedLanguages')}
        <input
          value={form.allowedLanguages}
          placeholder={t('admin.allowedLanguagesHint')}
          onChange={(event) => set('allowedLanguages', event.target.value)}
        />
      </label>
      <label>
        {t('admin.weekly')}
        <input
          type="number"
          min={0}
          value={form.weeklyAllowance}
          onChange={(event) => set('weeklyAllowance', event.target.value)}
        />
      </label>
      {error && <p role="alert">{error}</p>}
      <button type="submit">{t('admin.save')}</button>
      <button type="button" onClick={onCancel}>
        {t('admin.cancel')}
      </button>
    </form>
  )
}

type DeleteConfirmProps = {
  profile: KidProfile
  onCancel: () => void
  onDeleted: () => void
}

function DeleteConfirm({ profile, onCancel, onDeleted }: DeleteConfirmProps) {
  const t = useT()
  const [removeLabels, setRemoveLabels] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function confirm() {
    setError(null)
    try {
      await deleteKidProfile(profile.id, removeLabels)
      onDeleted()
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    }
  }

  return (
    <div
      role="alertdialog"
      aria-label={t('admin.deleteDialog', { name: profile.name })}
    >
      <p>{t('admin.deleteQuestion', { name: profile.name })}</p>
      <label>
        <input
          type="checkbox"
          checked={removeLabels}
          onChange={(event) => setRemoveLabels(event.target.checked)}
        />
        {t('admin.removeLabels')}
      </label>
      {error && <p role="alert">{error}</p>}
      <button type="button" onClick={() => void confirm()}>
        {t('admin.confirmDelete')}
      </button>
      <button type="button" onClick={onCancel}>
        {t('admin.keep')}
      </button>
    </div>
  )
}
