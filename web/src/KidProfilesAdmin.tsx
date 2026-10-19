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
  }
}

const toNumber = (value: string) => (value.trim() === '' ? null : Number(value))

function toInput(form: FormState): KidProfileInput {
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
  }
}

const messageOf = (failure: unknown) =>
  failure instanceof Error ? failure.message : 'Something went wrong'

export default function KidProfilesAdmin() {
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
      (failure: unknown) => current && setError(messageOf(failure)),
    )
    return () => {
      current = false
    }
  }, [version])

  async function unlock(profile: KidProfile) {
    setError(null)
    try {
      await unlockKidProfile(profile.id)
      load()
    } catch (failure) {
      setError(messageOf(failure))
    }
  }

  return (
    <section aria-labelledby="kid-profiles-heading">
      <h2 id="kid-profiles-heading">Kid profiles</h2>
      {error && !editing && <p role="alert">{error}</p>}
      <ul>
        {profiles.map((profile) => (
          <li key={profile.id}>
            <strong>{profile.name}</strong> ({profile.avatar})
            {profile.isLocked && <span> Locked</span>}
            <button type="button" onClick={() => setEditing(profile)}>
              Edit {profile.name}
            </button>
            {profile.isLocked && (
              <button type="button" onClick={() => void unlock(profile)}>
                Unlock {profile.name}
              </button>
            )}
            <button type="button" onClick={() => setDeleting(profile)}>
              Delete {profile.name}
            </button>
          </li>
        ))}
      </ul>
      {profiles.length === 0 && <p>No profiles yet.</p>}
      {editing === null && (
        <button type="button" onClick={() => setEditing('new')}>
          Add profile
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
      if (profile) {
        await updateKidProfile(profile.id, input)
      } else {
        await createKidProfile(input)
      }
      onSaved()
    } catch (failure) {
      setError(messageOf(failure))
    }
  }

  return (
    <form
      onSubmit={submit}
      aria-label={profile ? 'Edit profile' : 'New profile'}
    >
      <label>
        Name
        <input
          value={form.name}
          onChange={(event) => set('name', event.target.value)}
        />
      </label>
      <label>
        Avatar
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
        PIN
        <input
          type="password"
          inputMode="numeric"
          autoComplete="off"
          value={form.pin}
          placeholder={profile?.hasPin ? 'Unchanged' : '4 to 8 digits'}
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
          Remove PIN
        </label>
      )}
      <label>
        Movie age limit
        <input
          type="number"
          min={0}
          max={21}
          value={form.movieAgeLimit}
          onChange={(event) => set('movieAgeLimit', event.target.value)}
        />
      </label>
      <label>
        TV age limit
        <input
          type="number"
          min={0}
          max={21}
          value={form.tvAgeLimit}
          onChange={(event) => set('tvAgeLimit', event.target.value)}
        />
      </label>
      <label>
        Plex label
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
        Weekly requests
        <input
          type="number"
          min={0}
          value={form.weeklyAllowance}
          onChange={(event) => set('weeklyAllowance', event.target.value)}
        />
      </label>
      {error && <p role="alert">{error}</p>}
      <button type="submit">Save</button>
      <button type="button" onClick={onCancel}>
        Cancel
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
  const [removeLabels, setRemoveLabels] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function confirm() {
    setError(null)
    try {
      await deleteKidProfile(profile.id, removeLabels)
      onDeleted()
    } catch (failure) {
      setError(messageOf(failure))
    }
  }

  return (
    <div role="alertdialog" aria-label={`Delete ${profile.name}`}>
      <p>
        Delete {profile.name}? Their request history is kept, but they can no
        longer sign in.
      </p>
      <label>
        <input
          type="checkbox"
          checked={removeLabels}
          onChange={(event) => setRemoveLabels(event.target.checked)}
        />
        Also remove the Plex labels this profile owns
      </label>
      {error && <p role="alert">{error}</p>}
      <button type="button" onClick={() => void confirm()}>
        Confirm delete
      </button>
      <button type="button" onClick={onCancel}>
        Keep profile
      </button>
    </div>
  )
}
