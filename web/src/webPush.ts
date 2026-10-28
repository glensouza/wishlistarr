export class PushUnavailable extends Error {
  reason: 'unsupported' | 'denied'

  constructor(reason: 'unsupported' | 'denied') {
    super(reason)
    this.reason = reason
  }
}

export type BrowserSubscription = {
  endpoint: string
  p256dh: string
  auth: string
}

export function pushSupport(): 'ok' | 'unsupported' | 'denied' {
  if (
    typeof navigator === 'undefined' ||
    !('serviceWorker' in navigator) ||
    typeof window === 'undefined' ||
    !('PushManager' in window) ||
    typeof Notification === 'undefined'
  ) {
    return 'unsupported'
  }
  return Notification.permission === 'denied' ? 'denied' : 'ok'
}

function keyBytes(value: string) {
  const padded =
    value.replace(/-/g, '+').replace(/_/g, '/') +
    '='.repeat((4 - (value.length % 4)) % 4)
  const binary = atob(padded)
  const bytes = new Uint8Array(binary.length)
  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.charCodeAt(index)
  }
  return bytes
}

export async function subscribeBrowser(
  publicKey: string,
): Promise<BrowserSubscription> {
  const support = pushSupport()
  if (support !== 'ok') throw new PushUnavailable(support)
  const registration = await navigator.serviceWorker.register('/sw.js')
  await navigator.serviceWorker.ready
  let subscription = await registration.pushManager.getSubscription()
  if (!subscription) {
    try {
      subscription = await registration.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: keyBytes(publicKey),
      })
    } catch (error) {
      if (error instanceof DOMException && error.name === 'NotAllowedError') {
        throw new PushUnavailable('denied')
      }
      throw error
    }
  }
  const json = subscription.toJSON()
  const endpoint = json.endpoint
  const p256dh = json.keys?.p256dh
  const auth = json.keys?.auth
  if (!endpoint || !p256dh || !auth) {
    throw new PushUnavailable('unsupported')
  }
  return { endpoint, p256dh, auth }
}
