import {
  cleanup,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { resetAntiforgery } from './api'
import NotificationSettings, {
  type NotificationConfiguration,
} from './NotificationSettings'

type Call = {
  path: string
  method: string
  body: unknown
  headers?: HeadersInit
}
let calls: Call[]
let settings: NotificationConfiguration
let failure: 'load' | 'save' | 'test' | 'crash' | null

beforeEach(() => {
  resetAntiforgery()
  calls = []
  failure = null
  settings = {
    publicBaseUrl: 'https://wishlist.example',
    providers: [
      {
        provider: 'ntfy',
        enabled: true,
        events: ['pending'],
        configuration: { url: '', token: '' },
        hasSecret: true,
      },
      {
        provider: 'discord',
        enabled: false,
        events: ['approved'],
        configuration: {},
        hasSecret: true,
      },
      {
        provider: 'apprise',
        enabled: false,
        events: ['denied'],
        configuration: { url: '', targets: '' },
        hasSecret: true,
      },
      {
        provider: 'smtp',
        enabled: false,
        events: ['available', 'failed'],
        configuration: {
          host: 'smtp.example',
          port: 587,
          enableSsl: true,
          username: 'test',
          password: '',
          from: 'from@example.test',
          to: 'to@example.test',
        },
        hasSecret: true,
      },
    ],
  }
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path === '/api/v1/auth/antiforgery')
        return Response.json({
          token: 'test-token',
          headerName: 'X-CSRF-TOKEN',
        })
      const method = init?.method ?? 'GET'
      calls.push({
        path,
        method,
        body: init?.body ? JSON.parse(init.body as string) : undefined,
        headers: init?.headers,
      })
      if (failure === 'crash' && method !== 'GET') {
        return Response.json(
          { title: 'Internal detail must not be shown' },
          { status: 500 },
        )
      }
      if (failure === 'save' && method === 'PUT') {
        return Response.json(
          { title: 'SMTP authentication requires STARTTLS.' },
          { status: 400 },
        )
      }
      if (
        (failure === 'load' && method === 'GET') ||
        (failure === 'test' && path.endsWith('/test'))
      ) {
        return Response.json(
          {
            title:
              'The notification service rejected the saved credentials (HTTP 401).',
          },
          { status: 502 },
        )
      }
      if (method === 'GET' && path === '/api/v1/notifications')
        return Response.json(settings)
      return new Response(null, { status: 204 })
    }),
  )
})
afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

async function open() {
  const user = userEvent.setup()
  render(<NotificationSettings />)
  await user.click(screen.getByRole('button', { name: 'Manage notifications' }))
  await screen.findByLabelText('Public Wishlistarr URL')
  return user
}

it('loads only when opened and shows saved provider settings with secrets hidden', async () => {
  const user = userEvent.setup()
  render(<NotificationSettings />)
  expect(calls).toHaveLength(0)
  await user.click(screen.getByRole('button', { name: 'Manage notifications' }))
  const form = within(await screen.findByRole('group', { name: 'ntfy' }))
  expect(
    (form.getByLabelText('Enable this provider') as HTMLInputElement).checked,
  ).toBe(true)
  expect(
    (form.getByLabelText('Pending requests') as HTMLInputElement).checked,
  ).toBe(true)
  expect((form.getByLabelText('Access token') as HTMLInputElement).value).toBe(
    '',
  )
  expect(form.getByText('Secret configured (hidden).')).toBeTruthy()
  expect((form.getByLabelText('Service URL') as HTMLInputElement).type).toBe(
    'password',
  )
  expect((form.getByLabelText('Service URL') as HTMLInputElement).value).toBe(
    '',
  )
})

it('saves enabled and event selections, preserves blank secrets, and sends antiforgery', async () => {
  const user = await open()
  const form = within(screen.getByRole('group', { name: 'ntfy' }))
  await user.click(form.getByLabelText('Enable this provider'))
  await user.click(form.getByLabelText('Pending requests'))
  await user.click(form.getByLabelText('Approved requests'))
  await user.click(form.getByLabelText('Repeated PIN failures'))
  await user.click(form.getByLabelText('Locked profiles'))
  expect(
    (
      form.getByRole('button', {
        name: 'Send test notification',
      }) as HTMLButtonElement
    ).disabled,
  ).toBe(true)
  await user.click(form.getByRole('button', { name: 'Save provider' }))
  await screen.findByText('Settings saved.')
  const put = calls.find((call) => call.method === 'PUT')!
  expect(put).toMatchObject({
    path: '/api/v1/notifications/ntfy',
    body: {
      enabled: false,
      events: ['approved', 'pinFailures', 'profileLocked'],
      configuration: {},
    },
  })
  expect(put.headers).toMatchObject({ 'X-CSRF-TOKEN': 'test-token' })
})

it('explains Apprise endpoint and target behavior even when the saved URL is masked', async () => {
  const user = await open()
  const hint =
    'Enter the full /notify endpoint URL with optional targets. When no targets are saved, Apprise uses its server defaults. A /notify/{key} URL uses saved configuration and ignores targets.'
  expect(
    within(screen.getByRole('group', { name: 'ntfy' })).queryByText(hint),
  ).toBeNull()
  await user.click(screen.getByRole('button', { name: 'Apprise' }))
  const form = within(screen.getByRole('group', { name: 'Apprise' }))
  expect(form.getByText(hint)).toBeTruthy()
  const url = form.getByLabelText('Service URL') as HTMLInputElement
  const targets = form.getByLabelText('Apprise targets') as HTMLInputElement
  expect(url.value).toBe('')
  expect(url.getAttribute('aria-describedby')).toContain(
    'notifications-apprise-endpoint-hint',
  )
  expect(targets.getAttribute('aria-describedby')).toContain(
    'notifications-apprise-endpoint-hint',
  )
  expect(targets.disabled).toBe(false)
})

it.each([
  [
    'Discord',
    'discord',
    'Service URL',
    'https://discord.example/webhook',
    'url',
  ],
  ['Apprise', 'apprise', 'Apprise targets', 'json://example.test', 'targets'],
  ['Email (SMTP)', 'smtp', 'SMTP password', 'replacement-value', 'password'],
] as const)(
  'saves %s replacements and clears the secret input after saving',
  async (label, provider, field, secret, key) => {
    const user = await open()
    await user.click(screen.getByRole('button', { name: label }))
    const form = within(screen.getByRole('group', { name: label }))
    const input = form.getByLabelText(field) as HTMLInputElement
    expect(input.type).toBe('password')
    await user.type(input, secret)
    await user.click(form.getByLabelText('Enable this provider'))
    await user.click(form.getByRole('button', { name: 'Save provider' }))
    await screen.findByText('Settings saved.')
    expect(input.value).toBe('')
    expect(calls.find((call) => call.method === 'PUT')).toMatchObject({
      path: `/api/v1/notifications/${provider}`,
      body: { enabled: true, configuration: { [key]: secret } },
    })
    if (provider === 'smtp')
      expect(calls.find((call) => call.method === 'PUT')).toMatchObject({
        body: { configuration: { port: 587, enableSsl: true } },
      })
  },
)

it('retains drafts across provider selection and sends a bodyless test for each provider', async () => {
  const user = await open()
  const ntfy = within(screen.getByRole('group', { name: 'ntfy' }))
  await user.type(ntfy.getByLabelText('Access token'), 'replacement')
  await user.click(screen.getByRole('button', { name: 'Discord' }))
  await user.click(screen.getByRole('button', { name: 'ntfy' }))
  expect((ntfy.getByLabelText('Access token') as HTMLInputElement).value).toBe(
    'replacement',
  )
  await user.click(ntfy.getByRole('button', { name: 'Save provider' }))
  await screen.findByText('Settings saved.')
  for (const [label, provider] of [
    ['ntfy', 'ntfy'],
    ['Discord', 'discord'],
    ['Apprise', 'apprise'],
    ['Email (SMTP)', 'smtp'],
  ]) {
    await user.click(screen.getByRole('button', { name: label }))
    const form = within(screen.getByRole('group', { name: label }))
    await user.click(
      form.getByRole('button', { name: 'Send test notification' }),
    )
    await waitFor(() =>
      expect(form.getByRole('status').textContent).toBe(
        'Test notification sent.',
      ),
    )
    expect(
      calls.find(
        (call) => call.path === `/api/v1/notifications/${provider}/test`,
      ),
    ).toMatchObject({ method: 'POST', body: undefined })
  }
})

it('rejects non-HTTP public URLs and saves an absolute HTTPS base URL', async () => {
  const user = await open()
  const input = screen.getByLabelText('Public Wishlistarr URL')
  await user.clear(input)
  await user.type(input, 'ftp://example.test')
  await user.click(screen.getByRole('button', { name: 'Save public URL' }))
  expect((await screen.findByRole('alert')).textContent).toContain(
    'HTTP or HTTPS',
  )
  expect(calls.filter((call) => call.method === 'PUT')).toHaveLength(0)
  await user.clear(input)
  await user.type(input, 'https://wishlist.example:8080')
  await user.click(screen.getByRole('button', { name: 'Save public URL' }))
  await screen.findByText('Settings saved.')
  expect(calls.find((call) => call.method === 'PUT')).toMatchObject({
    path: '/api/v1/notifications/base-url',
    body: { url: 'https://wishlist.example:8080' },
  })
})

it.each([
  'https://example.test/path',
  'https://example.test?key=value',
  'https://example.test#fragment',
  'https://user:pass@example.test',
])('rejects an invalid approval link origin: %s', async (url) => {
  const user = await open()
  const input = screen.getByLabelText('Public Wishlistarr URL')
  await user.clear(input)
  await user.type(input, url)
  await user.click(screen.getByRole('button', { name: 'Save public URL' }))
  expect((await screen.findByRole('alert')).textContent).toContain('origin')
  expect(calls.filter((call) => call.method === 'PUT')).toHaveLength(0)
})

it('retries loading after an error', async () => {
  failure = 'load'
  const user = userEvent.setup()
  render(<NotificationSettings />)
  await user.click(screen.getByRole('button', { name: 'Manage notifications' }))
  expect((await screen.findByRole('alert')).textContent).toBe(
    'We could not load notification settings.',
  )
  failure = null
  await user.click(screen.getByRole('button', { name: 'Try again' }))
  await screen.findByLabelText('Public Wishlistarr URL')
})

it('sends the displayed SMTP defaults when configuring a provider for the first time', async () => {
  settings.providers = []
  const user = await open()
  await user.click(screen.getByRole('button', { name: 'Email (SMTP)' }))
  const form = within(screen.getByRole('group', { name: 'Email (SMTP)' }))
  expect((form.getByLabelText('SMTP port') as HTMLInputElement).value).toBe(
    '587',
  )
  expect(
    (form.getByLabelText('Use STARTTLS') as HTMLInputElement).checked,
  ).toBe(true)
  await user.click(form.getByRole('button', { name: 'Save provider' }))
  await screen.findByText('Settings saved.')
  expect(calls.find((call) => call.method === 'PUT')).toMatchObject({
    body: { configuration: { port: 587, enableSsl: true } },
  })
})

it('keeps the public URL draft and offers a retry after save failure', async () => {
  const user = await open()
  const input = screen.getByLabelText(
    'Public Wishlistarr URL',
  ) as HTMLInputElement
  await user.clear(input)
  await user.type(input, 'https://changed.example')
  failure = 'save'
  await user.click(screen.getByRole('button', { name: 'Save public URL' }))
  expect((await screen.findByRole('alert')).textContent).toContain(
    'could not save',
  )
  expect(input.value).toBe('https://changed.example')
  failure = null
  await user.click(screen.getByRole('button', { name: 'Save public URL' }))
  await screen.findByText('Settings saved.')
})

it('shows the server reason for save and test errors, keeping edits available for retry', async () => {
  const user = await open()
  const form = within(screen.getByRole('group', { name: 'ntfy' }))
  failure = 'save'
  await user.type(form.getByLabelText('Access token'), 'replacement')
  await user.click(form.getByRole('button', { name: 'Save provider' }))
  const saveAlert = (await form.findByRole('alert')).textContent
  expect(saveAlert).toContain('could not save')
  expect(saveAlert).toContain('SMTP authentication requires STARTTLS.')
  expect((form.getByLabelText('Access token') as HTMLInputElement).value).toBe(
    'replacement',
  )
  failure = null
  await user.click(form.getByRole('button', { name: 'Save provider' }))
  await screen.findByText('Settings saved.')
  failure = 'test'
  await user.click(form.getByRole('button', { name: 'Send test notification' }))
  const testAlert = (await form.findByRole('alert')).textContent
  expect(testAlert).toContain('test notification failed')
  expect(testAlert).toContain('rejected the saved credentials (HTTP 401)')
  await waitFor(() =>
    expect(
      (
        form.getByRole('button', {
          name: 'Send test notification',
        }) as HTMLButtonElement
      ).disabled,
    ).toBe(false),
  )
})

it('never shows the detail of an unexpected server failure', async () => {
  const user = await open()
  const form = within(screen.getByRole('group', { name: 'ntfy' }))
  failure = 'crash'
  await user.click(form.getByRole('button', { name: 'Send test notification' }))
  expect((await form.findByRole('alert')).textContent).toContain(
    'test notification failed',
  )
  expect(screen.queryByText(/Internal detail/)).toBeNull()
})

it('announces results through a status region that is already present', async () => {
  const user = await open()
  const form = within(screen.getByRole('group', { name: 'ntfy' }))
  const status = form.getByRole('status')
  expect(status.textContent).toBe('')
  await user.click(form.getByRole('button', { name: 'Send test notification' }))
  await waitFor(() =>
    expect(status.textContent).toBe('Test notification sent.'),
  )
})

it('leaves a cleared SMTP port to the server default instead of sending zero', async () => {
  const user = await open()
  await user.click(screen.getByRole('button', { name: 'Email (SMTP)' }))
  const form = within(screen.getByRole('group', { name: 'Email (SMTP)' }))
  await user.clear(form.getByLabelText('SMTP port'))
  await user.click(form.getByRole('button', { name: 'Save provider' }))
  await form.findByText('Settings saved.')
  const saved = calls.find((call) => call.path === '/api/v1/notifications/smtp')
    ?.body as { configuration: Record<string, unknown> }
  expect(saved.configuration).not.toHaveProperty('port')
  expect(saved.configuration.host).toBe('smtp.example')
})
