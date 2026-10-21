# Inbound webhooks

Plex, Sonarr, Radarr and Seerr can notify Wishlistarr when something changes instead of waiting for the next poll. Each source has its own address that contains a secret token.

Plex webhooks require Plex Pass on the account that owns the server. Without Plex Pass, or when a webhook is missed, polling remains the fallback; webhooks only make updates arrive sooner.

## Addresses

A parent can list the address of every source with `GET /api/v1/settings/webhooks`:

```json
[
  { "source": "plex", "url": "https://wishlistarr.example/api/v1/webhooks/plex/<token>" },
  { "source": "sonarr", "url": "https://wishlistarr.example/api/v1/webhooks/sonarr/<token>" },
  { "source": "radarr", "url": "https://wishlistarr.example/api/v1/webhooks/radarr/<token>" },
  { "source": "seerr", "url": "https://wishlistarr.example/api/v1/webhooks/seerr/<token>" }
]
```

The host in the address is the one the settings request was made with, so open the settings through the address your other services use to reach Wishlistarr. Tokens are generated on first start, stored encrypted and never appear in `GET /api/v1/settings`.

`POST /api/v1/settings/webhooks/{source}/regenerate` replaces the token of one source. The old address stops working immediately, so update the address in the sending application afterwards. These endpoints require a parent.

## Configuring the senders

- Plex: Settings, Webhooks, Add Webhook. Plex sends `multipart/form-data` with the event in a `payload` part; other parts such as the thumbnail are ignored.
- Sonarr and Radarr: Settings, Connect, Webhook, method `POST`, with the address as the URL.
- Seerr: Settings, Notifications, Webhook, with the address as the webhook URL and the default JSON payload.

## Behaviour

- The receiver is `POST /api/v1/webhooks/{source}/{token}`. It needs no sign-in; the token is the credential. The token is compared in constant time.
- An unknown source returns `404`, a missing or wrong token returns `401`, an invalid payload returns `400` and a payload over the size limit returns `413`.
- The event type is read from `event` (Plex), `eventType` (Sonarr and Radarr) or `notification_type` (Seerr), and is `unknown` when the field is missing.
- A delivery whose payload is identical to one already received from the same source within the deduplication window is acknowledged with `200` and `{"status":"duplicate"}` and is not processed again. A new delivery returns `202`.
- Accepted events are stored and handed to the `webhook-events` background job, which also runs every minute to pick up anything left over. The Plex `library.new` event refreshes the library index (see [library.md](library.md)); other events are only logged.
- Tokens are not written to the logs: request paths are logged as `/api/v1/webhooks/{source}/***`. Avoid logging full URLs in a reverse proxy in front of Wishlistarr.

## Configuration

```json
{
  "Webhooks": {
    "MaxPayloadBytes": 2097152,
    "DeduplicationWindow": "01:00:00",
    "Retention": "7.00:00:00"
  }
}
```

`MaxPayloadBytes` limits the request body, `DeduplicationWindow` is how long an identical delivery counts as a duplicate, and `Retention` is how long processed events are kept. `Retention` must not be shorter than `DeduplicationWindow`.
