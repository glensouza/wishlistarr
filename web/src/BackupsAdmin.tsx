import { useEffect, useRef, useState, type FormEvent } from 'react'
import {
  downloadBackupPath,
  latestBackupRun,
  listBackups,
  restoreBackup,
  startBackup,
  type BackupItem,
} from './backups'
import { useT } from './i18n'

const messageOf = (failure: unknown, fallback: string) =>
  failure instanceof Error ? failure.message : fallback

const formatTime = (value: string) =>
  new Date(value).toLocaleString(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })

export default function BackupsAdmin() {
  const t = useT()
  const [backups, setBackups] = useState<BackupItem[]>([])
  const [retention, setRetention] = useState<number | null>(null)
  const [restorePending, setRestorePending] = useState(false)
  const [working, setWorking] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [version, setVersion] = useState(0)
  const [confirming, setConfirming] = useState<File | null>(null)
  const restoreButton = useRef<HTMLButtonElement>(null)
  const dialog = useRef<HTMLDivElement>(null)
  const cancelButton = useRef<HTMLButtonElement>(null)
  const returnFocus = useRef(false)

  useEffect(() => {
    let current = true
    listBackups().then(
      (loaded) => {
        if (!current) return
        if (
          !loaded ||
          !Array.isArray(loaded.backups) ||
          typeof loaded.retention !== 'number' ||
          typeof loaded.restorePending !== 'boolean'
        ) {
          setError(t('app.somethingWrong'))
          return
        }
        setBackups(loaded.backups)
        setRetention(loaded.retention)
        setRestorePending(loaded.restorePending)
      },
      (failure: unknown) =>
        current && setError(messageOf(failure, t('app.somethingWrong'))),
    )
    return () => {
      current = false
    }
  }, [version, t])

  useEffect(() => {
    if (!confirming) return
    cancelButton.current?.focus()
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault()
        returnFocus.current = true
        setConfirming(null)
        return
      }
      if (event.key !== 'Tab') return
      const buttons = Array.from(
        dialog.current?.querySelectorAll<HTMLButtonElement>('button') ?? [],
      )
      if (buttons.length === 0) return
      const first = buttons[0]
      const last = buttons[buttons.length - 1]
      const active = document.activeElement
      if (!dialog.current?.contains(active)) {
        event.preventDefault()
        first.focus()
      } else if (event.shiftKey && active === first) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && active === last) {
        event.preventDefault()
        first.focus()
      }
    }
    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [confirming])

  useEffect(() => {
    if (confirming || working || !returnFocus.current) return
    returnFocus.current = false
    restoreButton.current?.focus()
  }, [confirming, working])

  async function backUpNow() {
    setError(null)
    setWorking(true)
    try {
      const started = await startBackup()
      for (let attempt = 0; attempt < 40; attempt++) {
        const run = await latestBackupRun(started.runId)
        if (run && run.outcome !== 'running') {
          if (run.outcome !== 'succeeded') throw new Error(t('backups.failed'))
          setVersion((current) => current + 1)
          return
        }
        await new Promise((resolve) => setTimeout(resolve, 250))
      }
      throw new Error(t('backups.failed'))
    } catch (failure) {
      setError(messageOf(failure, t('app.somethingWrong')))
    } finally {
      setWorking(false)
    }
  }

  function askToRestore(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const input = event.currentTarget.elements.namedItem('backup')
    const file =
      input instanceof HTMLInputElement && input.files ? input.files[0] : null
    if (!file) return
    setError(null)
    setConfirming(file)
  }

  async function restore(file: File) {
    returnFocus.current = true
    setConfirming(null)
    setError(null)
    setWorking(true)
    try {
      await restoreBackup(file)
      setRestorePending(true)
      setVersion((current) => current + 1)
    } catch (failure) {
      setError(messageOf(failure, t('backups.restoreFailed')))
    } finally {
      setWorking(false)
    }
  }

  return (
    <section>
      <h2>{t('backups.title')}</h2>
      <p className="notice">{t('backups.warning')}</p>
      {retention !== null && (
        <p className="muted">{t('backups.retention', { count: retention })}</p>
      )}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {restorePending && <p role="status">{t('backups.pending')}</p>}
      <button type="button" onClick={() => void backUpNow()} disabled={working}>
        {working ? t('backups.working') : t('backups.now')}
      </button>
      {backups.length === 0 ? (
        <p>{t('backups.none')}</p>
      ) : (
        <ul className="backup-list">
          {backups.map((backup) => (
            <li key={backup.name}>
              <span>{formatTime(backup.createdAt)}</span>{' '}
              <a href={downloadBackupPath(backup.name)} download={backup.name}>
                {t('backups.download')}
              </a>
            </li>
          ))}
        </ul>
      )}
      <form onSubmit={askToRestore}>
        <label>
          {t('backups.file')}
          <input name="backup" type="file" accept=".zip,application/zip" />
        </label>
        <button
          ref={restoreButton}
          type="submit"
          disabled={working || confirming !== null}
        >
          {t('backups.restore')}
        </button>
      </form>
      {confirming && (
        <div
          ref={dialog}
          role="alertdialog"
          aria-modal="true"
          aria-label={t('backups.confirmTitle')}
        >
          <p>{t('backups.confirmQuestion', { name: confirming.name })}</p>
          <button type="button" onClick={() => void restore(confirming)}>
            {t('backups.confirmRestore')}
          </button>
          <button
            ref={cancelButton}
            type="button"
            onClick={() => {
              returnFocus.current = true
              setConfirming(null)
            }}
          >
            {t('backups.cancelRestore')}
          </button>
        </div>
      )}
    </section>
  )
}
