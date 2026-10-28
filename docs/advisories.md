# Content advisories

Parents can see three kinds of advisory on a title, and can use two of them as content-policy blocks. The sources are separate. DoesTheDogDie data is not TMDB data, and a parent note is neither.

## Sources in use

### TMDB certification descriptors

The existing TMDB client already returns content descriptors with the certification it selects from movie `release_dates` and TV `content_ratings`. Wishlistarr does not call TMDB a second time for them and does not change which certification is selected. The parent approval queue shows those descriptors on each card. They are not written to the database, so the six-month retention in [TMDB](tmdb.md) does not gain a new stored field. A profile can block descriptor text. Comparison is trimmed and case-insensitive. See [Content policy](content-policy.md).

### DoesTheDogDie topic stats

When a parent has stored their own DoesTheDogDie API key, Wishlistarr reads community topic stats for a title. The API is `https://www.doesthedogdie.com/api/v3`. Every request sends the key in the `X-API-KEY` header and never in the query string. A title is looked up with `GET /api/v3/items?tmdb={id}`. The client reads a JSON array of items with `id`, `itemTypeId`, `itemTypeName` and `tmdbId`. Movie items use item type id 15 and TV items use 16. A lookup can return more than one item; the first item whose type matches the request kind is kept. If none match, the title has no DoesTheDogDie item. Item detail is `GET /api/v3/items/{itemId}`. `topicItemStats` (`topicId`, `topicName`, `yesSum`, `noSum`, `numComments`, `itemId`) is the warning list. `/ratings`, `/topics`, Scene Alerts and any write back to DoesTheDogDie are not called.

A topic is shown, or matched against a block, only when `yesSum` is greater than `noSum` and `yesSum` is greater than 0. Names are trimmed, blank names are dropped, and the same name is kept once (the higher `yesSum` wins). The list is sorted by `yesSum` descending, then by name. Vote counts, comments and timestamps are not shown.

Errors are JSON `{ "error", "message" }`. `401` is `missing_api_key` or `invalid_api_key`. `403` is `upgrade_required`. `429` is `rate_limit_exceeded` or `monthly_limit_exceeded` and may include `Retry-After`. `404` is `not_found`. The free tier allows 30 requests a minute and 5000 a month. `429` is retried the way the TMDB client retries, with each wait capped at 2 seconds and the total wait capped at 4 seconds, so the approval queue still returns. A failed lookup is a gap, not a failed queue request. The key check uses `GET /api/v3/items?tmdb=0` and treats `200` or `404` as a key the service accepted. It does not cache that call.

The operator supplies the key. None is shipped. The DoesTheDogDie terms describe the free tier as non-commercial. Store the key as `dtdd.api_key` with `PUT /api/v1/settings/dtdd.api_key`. It is always encrypted, whatever `isSecret` says, it is never returned by the settings API, and it is never written to logs. An empty value deletes the key. `POST /api/v1/settings/dtdd/test` checks the stored key. Both routes are parent only. With no key saved, Wishlistarr makes no DoesTheDogDie call. The approval queue still loads, and approve and deny still work.

Their terms allow a local cache only to improve performance, require it to be refreshed at least every 30 days, and require it to be deleted when the key is removed. Wishlistarr keeps the cache in memory for at most 7 days and does not write DoesTheDogDie rows to SQLite. The cache is cleared when the key is removed or replaced. It is not TMDB data and is not covered by the six-month retention.

Wherever their data is displayed, the visible credit is "Powered by DoesTheDogDie.com" and the link is `https://www.doesthedogdie.com`. That credit is on the About page and on an approval card that is showing topic names. It is not shown for a lookup that failed, and it is not shown when no topic met the vote rule. Kid mode does not show the link or the warnings. See [Attribution](attribution.md).

A kid decision calls DoesTheDogDie only when a key is saved and that profile blocks at least one topic, and only when the title is not already on an explicit allow or block list. Kid search does not call it otherwise. A failed load is not replaced with an empty list. See [Content policy](content-policy.md).

### Parent notes

A parent can write one note per title, keyed by media kind and TMDB id. The same note is shown for every kid profile. Only a parent session can read or write it (`GET` and `PUT /api/v1/titles/{kind}/{tmdbId}/note`). A kid session, a device session and an anonymous caller cannot. Surrounding space is removed. An empty note deletes the stored note. A note longer than 2000 characters is rejected, and nothing longer than that is stored. The note is not TMDB data and not DoesTheDogDie data. It is not a policy input, and it is not the denial note a kid can see.

Editing the note is part of the parent approval view. Saving refreshes the note on every card for that title. Kid routes do not receive it.

## Sources not used

- Common Sense Media: there is no public API for this use, and scraping is not allowed.
- IMDb parents guide: it is not available for this use, and scraping is not allowed.
- Kids-In-Mind: there is no API, and the reviews are copyrighted.
- DoesTheDogDie Scene Alerts, per-topic comments, timestamps, and any write back to DoesTheDogDie.
