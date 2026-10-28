self.addEventListener('push', (event) => {
  let title = 'Wishlistarr'
  let body = 'Something new is ready to watch.'
  try {
    const payload = event.data ? event.data.json() : null
    if (payload && typeof payload.title === 'string' && payload.title) {
      title = payload.title
    }
    if (payload && typeof payload.body === 'string' && payload.body) {
      body = payload.body
    }
  } catch {
    // A push the browser cannot parse stays generic, so a title is never invented here.
  }
  event.waitUntil(self.registration.showNotification(title, { body }))
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  event.waitUntil(
    self.clients
      .matchAll({ type: 'window', includeUncontrolled: true })
      .then((clients) => {
        for (const client of clients) {
          if ('focus' in client) return client.focus()
        }
        return self.clients.openWindow('/')
      }),
  )
})
