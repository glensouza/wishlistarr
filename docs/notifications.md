# Parent notifications

Open **Parent area → Notifications** to save the public Wishlistarr origin, then configure ntfy, Discord, Apprise, or SMTP. Each provider has its own enabled switch, event selection, and **Send test** button. Save changes before testing. Tests send immediately even when a provider is disabled. The public origin is used for links; request Host headers never determine notification destinations.

Subscriptions belong to the signed-in parent account. Version 1 has one server-owner parent and no invitations or additional roles. API keys and trusted-network access represent that owner only while exactly one parent exists. Signed approval links always require the recipient's parent login session.

- **ntfy:** Enter the full topic URL, such as `https://ntfy.example.invalid/private-topic`. An optional bearer token is supported. Messages are UTF-8 plain text sent by POST to that topic.
- **Discord:** Enter the complete webhook URL. Existing query parameters such as `thread_id` are preserved. Delivery waits for Discord confirmation, disables mentions, and suppresses embeds.
- **Apprise:** Enter the complete notification endpoint: `/notify/` for stateless delivery or `/notify/config-key` for a saved configuration; a proxy path prefix is supported. Stateless target URLs are optional and sent as `urls` only when configured. If no targets are configured, the `urls` field is omitted so the server can use `APPRISE_STATELESS_URLS`; the server must have defaults configured. A saved-configuration endpoint never receives `urls`, and saving that endpoint clears inapplicable local targets. This avoids version-dependent handling of unused `urls` fields, including stricter validation in Apprise API 2.0.0. HTTP 200 indicates success; redirects and partial failures are failures. Deployments requiring HTTP Basic authentication are not currently supported.
- **SMTP:** Configure host, port, sender and recipient. **STARTTLS** upgrades the connection and validates the server certificate; SMTP authentication requires it. Implicit TLS on port 465 is not supported. Unauthenticated local relays may disable STARTTLS. No system credentials are used.

Provider configuration is encrypted with the application's existing Data Protection keys. Secret fields and HTTP destination URLs are masked in responses. Empty secret inputs preserve saved values. Keep the database and encryption keys together when backing up or restoring the application. Use HTTPS for externally reachable endpoints.

Request events include pending, approved, denied, available, and failed. Repeated PIN failures and profile locks are also selectable. New subscriptions start with future request events. Disabling a subscription cancels queued notifications when the delivery job next runs. Delivered messages necessarily leave the application and are subject to the destination service's retention settings.

## Approval links

Pending notifications contain signed approve and deny links. Both choices share a single grant bound to the request and recipient, valid for 48 hours from first delivery preparation. A GET only renders a confirmation page: previews, unfurlers and repeated opens cannot consume a link or decide a request. If the browser is signed out, open parent sign-in and then use **Continue after signing in** on the confirmation page. A signed-in browser may also need this step when arriving from another site because the login cookie uses SameSite Strict.

Only the intended parent's authenticated, antiforgery-protected POST can consume the grant. The confirmation explicitly identifies the request number and action. The decision uses the same approval service as the parent request queue. Replays, changed tokens, expired grants and already-decided requests are rejected. If processing fails after consumption or the application stops at that point, review and retry the request in the parent area; the link stays consumed. Pages forbid caching, embedding and referrer disclosure.

## Delivery and retries

The `notifications` background job runs every minute by default and can be triggered through the existing Jobs API. Request history acts as the durable event source. The job stores delivery records and its per-subscription event cursor in one database transaction, then sends queued records. PIN/security events are queued when raised. Each record has at most eight delivery attempts, with delays of 1, 2, 4, 8, 16, 32, and 60 minutes. A failure logs only the delivery ID, provider and attempt; provider response bodies, destinations and credentials are not logged. Failed manual test sends return a safe error and are not automatically retried.

Successful or exhausted delivery records are removed after 30 days. Exhausted records are retained during that period for diagnosis; fix the configuration and use the test button to confirm recovery. Delivery is at least once: if a destination accepts a message before the process stops or the response is lost, a retry can duplicate that message. Its approval links still share the same single-use grant. The application should run as a single instance against its SQLite database, as required by the existing job runner.
