import { useT, type MessageKey } from '../i18n'
import { useTheme, type ThemePreference } from './ThemeProvider'

const options: { value: ThemePreference; label: MessageKey }[] = [
  { value: 'system', label: 'theme.system' },
  { value: 'light', label: 'theme.light' },
  { value: 'dark', label: 'theme.dark' },
]

export default function ThemeToggle() {
  const t = useT()
  const { preference, setPreference } = useTheme()
  return (
    <label className="theme-toggle">
      <span className="visually-hidden">{t('theme.label')}</span>
      <select
        value={preference}
        onChange={(event) =>
          setPreference(event.target.value as ThemePreference)
        }
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {t(option.label)}
          </option>
        ))}
      </select>
    </label>
  )
}
