import { useEffect, useState, type FormEvent } from 'react'
import { useAuthStatus } from './authStatus'
import { useT } from './i18n'
import {
  createKidDevice,
  listKidDevices,
  newPairingCode,
  revokeKidDevice,
  updateKidDevice,
  type KidDevice,
  type PairingCode,
} from './kidDevices'
import { listKidProfiles, type KidProfile } from './kidProfiles'

const messageOf = (failure: unknown, fallback: string) =>
  failure instanceof Error ? failure.message : fallback

const formatTime = (value: string) =>
  new Date(value).toLocaleString(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })

type ShownCode = PairingCode & { name: string }

export default function KidDevicesAdmin() {
  const t = useT()
  const { refresh } = useAuthStatus()
  const [devices, setDevices] = useState<KidDevice[]>([])
  const [profiles, setProfiles] = useState<KidProfile[]>([])
  const [editing, setEditing] = useState<KidDevice | 'new' | null>(null)
  const [revoking, setRevoking] = useState<KidDevice | null>(null)
  const [shownCode, setShownCode] = useState<ShownCode | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [version, setVersion] = useState(0)
  const load = () => setVersion((current) => current + 1)

  useEffect(() => {
    let current = true
    Promise.all([listKidDevices(), listKidProfiles()]).then(
      ([loadedDevices, loadedProfiles]) => {
        if (!current) return
        setDevices(loadedDevices)
        setProfiles(loadedProfiles)
      },
      (failure: unknown) =>
        current && setError(messageOf(failure, t('app.somethingWrong'))),
    )
    return () => {
      current = false
    }
  }, [version, t])

  const profileNames = (device: KidDevice) =>
    profiles
      .filter((profile) => device.profileIds.includes(profile.id))
      .map((profile) => profile.name)
      .join(', ')

  function describe(device: KidDevice) {
    if (device.isPaired) {
      return device.lastSeenAt
        ? t('devices.lastSeen', { time: formatTime(device.lastSeenAt) })
        : t('devices.paired')
    }
    return device.pairingCodeExpiresAt
      ? t('devices.waiting', { time: formatTime(device.pairingCodeExpiresAt) })
      : t('devices.notPaired')
  }

  async function issueCode(device: KidDevice) {
    setError(null)
    try {
      const code = await newPairingCode(device.id)
      setShownCode({ ...code, name: device.name })
      load()
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    }
  }

  return (
    <section aria-labelledby="kid-devices-heading">
      <h2 id="kid-devices-heading">{t('devices.title')}</h2>
      <p className="muted">{t('devices.intro')}</p>
      {error && !editing && <p role="alert">{error}</p>}
      {shownCode && (
        <div role="status" className="pairing-code-panel">
          <h3>{t('devices.codeTitle', { name: shownCode.name })}</h3>
          <p className="pairing-code">{shownCode.code}</p>
          <p>
            {t('devices.codeIntro', {
              time: formatTime(shownCode.expiresAt),
            })}
          </p>
          <button type="button" onClick={() => setShownCode(null)}>
            {t('devices.codeDone')}
          </button>
        </div>
      )}
      <ul>
        {devices.map((device) => (
          <li key={device.id}>
            <strong>{device.name}</strong>
            {device.isCurrent && <span> ({t('devices.current')})</span>}
            <span> {describe(device)}</span>
            <span>
              {' '}
              {device.profileIds.length === 0
                ? t('devices.noAssigned')
                : t('devices.profileList', { names: profileNames(device) })}
            </span>
            <button type="button" onClick={() => setEditing(device)}>
              {t('devices.edit', { name: device.name })}
            </button>
            <button type="button" onClick={() => void issueCode(device)}>
              {t('devices.newCode', { name: device.name })}
            </button>
            <button type="button" onClick={() => setRevoking(device)}>
              {t('devices.revoke', { name: device.name })}
            </button>
          </li>
        ))}
      </ul>
      {devices.length === 0 && <p>{t('devices.none')}</p>}
      {editing === null && (
        <button type="button" onClick={() => setEditing('new')}>
          {t('devices.add')}
        </button>
      )}
      {editing !== null && (
        <DeviceForm
          key={editing === 'new' ? 'new' : editing.id}
          device={editing === 'new' ? null : editing}
          profiles={profiles}
          onCancel={() => setEditing(null)}
          onSaved={(code) => {
            setEditing(null)
            if (code) setShownCode(code)
            load()
          }}
        />
      )}
      {revoking && (
        <RevokeConfirm
          device={revoking}
          onCancel={() => setRevoking(null)}
          onRevoked={() => {
            setRevoking(null)
            if (revoking.isCurrent) refresh()
            load()
          }}
        />
      )}
    </section>
  )
}

type DeviceFormProps = {
  device: KidDevice | null
  profiles: KidProfile[]
  onCancel: () => void
  onSaved: (code: ShownCode | null) => void
}

function DeviceForm({ device, profiles, onCancel, onSaved }: DeviceFormProps) {
  const t = useT()
  const [name, setName] = useState(device?.name ?? '')
  const [selected, setSelected] = useState<number[]>(device?.profileIds ?? [])
  const [error, setError] = useState<string | null>(null)

  function toggle(id: number, checked: boolean) {
    setSelected((current) =>
      checked
        ? [...current, id]
        : current.filter((existing) => existing !== id),
    )
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    try {
      const input = { name, profileIds: selected }
      if (device) {
        await updateKidDevice(device.id, input)
        onSaved(null)
      } else {
        const created = await createKidDevice(input)
        onSaved({
          code: created.code,
          expiresAt: created.expiresAt,
          name: created.device.name,
        })
      }
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    }
  }

  return (
    <form
      onSubmit={submit}
      aria-label={device ? t('devices.editForm') : t('devices.newForm')}
    >
      <label>
        {t('devices.name')}
        <input value={name} onChange={(event) => setName(event.target.value)} />
      </label>
      <fieldset>
        <legend>{t('devices.profiles')}</legend>
        {profiles.length === 0 && <p>{t('devices.noProfiles')}</p>}
        {profiles.map((profile) => (
          <label key={profile.id}>
            <input
              type="checkbox"
              checked={selected.includes(profile.id)}
              onChange={(event) => toggle(profile.id, event.target.checked)}
            />
            {profile.name}
          </label>
        ))}
      </fieldset>
      {error && <p role="alert">{error}</p>}
      <button type="submit">{t('admin.save')}</button>
      <button type="button" onClick={onCancel}>
        {t('admin.cancel')}
      </button>
    </form>
  )
}

type RevokeConfirmProps = {
  device: KidDevice
  onCancel: () => void
  onRevoked: () => void
}

function RevokeConfirm({ device, onCancel, onRevoked }: RevokeConfirmProps) {
  const t = useT()
  const [error, setError] = useState<string | null>(null)

  async function confirm() {
    setError(null)
    try {
      await revokeKidDevice(device.id)
      onRevoked()
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    }
  }

  return (
    <div
      role="alertdialog"
      aria-label={t('devices.revokeDialog', { name: device.name })}
    >
      <p>{t('devices.revokeQuestion', { name: device.name })}</p>
      {error && <p role="alert">{error}</p>}
      <button type="button" onClick={() => void confirm()}>
        {t('devices.confirmRevoke')}
      </button>
      <button type="button" onClick={onCancel}>
        {t('devices.keep')}
      </button>
    </div>
  )
}
