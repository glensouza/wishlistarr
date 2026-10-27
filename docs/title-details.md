# Title details

A signed-in kid opens one movie or show from search at `/title/{kind}/{tmdbId}`. `GET /api/v1/kid/titles/{kind}/{tmdbId}` needs a Kid session and always answers for that profile. `kind` is `movie` or `show` (any other kind, or a TMDB id below 1, is `400` and TMDB is not called). Responses are sent with `Cache-Control: private, no-store`, including errors.

The state of an open request is shown on the request button: `available` means the title is on the server but not yet labeled for this kid, and reads "Almost ready". "Ready to watch" is only the library badge.

The screen shows the backdrop, poster, synopsis, cast, age rating, runtime, an availability badge and a request button. Shows also list seasons. Images come from the kid image proxy described in [Kid search](kid-search.md). That proxy serves `w185`, `w342` and `w1280` for any allowed TMDB file name, not only files linked from allowed titles; see [Kid search](kid-search.md) for why that is acceptable. A path that is not an allowed file name is left out of the response, so the screen never receives a remote image URL. When artwork is missing, or the image fails to load, the screen shows a plain block.

## Response

`{ kind, tmdbId, title, year, overview, posterUrl, backdropUrl, runtimeMinutes, rating, availability, needsParent, trailerKey, cast, seasons, request }`.

- `runtimeMinutes` is the movie runtime, or the first positive episode runtime of a show. Zero or missing becomes `null`.
- `rating` is the US certification, or `null` when TMDB has none.
- `cast` is at most 12 people: `name`, `character` and `profileUrl`. A blank name is skipped.
- `seasons` is empty for a movie. For a show it is every season TMDB returned, including season 0, ordered by season number. A negative season number is dropped, and a repeated number is kept once. Each season has `number`, `name`, `overview`, `posterUrl`, `episodeCount` and `year`. A blank name is `null` (the screen says "Specials" for season 0 and "Season N" otherwise). A negative episode count is stored as 0.
- `request` is the open request for this profile and title, `{ state, requestCount }`, or `null` when there is none. A denied request is not open: it is left out so the kid can ask again. The kid cannot move a request to another state from this screen. Asking uses [`POST /api/v1/kid/requests`](requests.md#asking-for-a-title) with `{ kind, tmdbId }` only.
- `availability` is `inLibrary` or `notInLibrary` when a library index can answer, and `null` when there is no index or it has not synced yet. This screen does not ask Plex. Search uses the same rule; requesting a title still uses the library lookup in [Requests](requests.md#library-lookup), which can fall back to a live Plex query.

Overviews are trimmed and capped at 2000 characters.

## Content policy

The title is checked with the same content policy, US certification and keyword rules as [search](kid-search.md#filtering) and [requests](requests.md#asking-for-a-title). Keywords are read only when the profile blocks keywords. A profile with no keyword blocks does not call that endpoint.

Fail closed:

- A blocked title, an adult title, a title with no name, a title TMDB does not have, a certification lookup that fails, or a keyword lookup that fails when the profile blocks keywords, all answer `404` with `We couldn't find that one.` That is the same answer as an id that does not exist. The body does not include the title, the rating, the reason or any artwork, and credits and videos are not requested. The reason code is written to the log.
- A title that needs approval is returned so the kid can ask, but masked: `overview`, `posterUrl`, `backdropUrl` and `trailerKey` are `null`, `cast` is empty and `needsParent` is `true`. Season numbers, episode counts and years stay; season names, overviews and posters are `null`. Credits and videos are not requested.
- An allowed title is returned in full. If the credits or the video list cannot be read, the title is still returned with an empty cast or `trailerKey: null`.

A missing or archived profile answers `403`. TMDB answering anything other than a clean miss on the details call answers `502` (`That page isn't working right now.`). No API key answers `503` with the same kid-safe title. Cancelling the request is not turned into either of those.

## Seasons

The kid can select a season. The screen says that access is for the whole show, not one season. The selection stays in the browser and is not sent with the request. A show with no seasons still shows that note.

## YouTube trailers

TMDB's video list supplies the trailer. A video is kept only when `site` is exactly `YouTube`, `type` is exactly `Trailer`, and `key` matches `^[A-Za-z0-9_-]{11}$` (the id shape the IFrame Player API embeds). The first official trailer wins; otherwise the first trailer. Vimeo, clips, teasers and any other key are ignored. The response contains the key and no YouTube URL.

Trailers are off unless a parent has stored `kids.youtube_trailers` as `true`. A missing value, a blank value, or anything that is not `true` or `false` is off. `PUT /api/v1/settings/kids.youtube_trailers` accepts only `true` or `false` (trimmed, any case), stores the canonical `true` or `false`, and never stores it as a secret. Anything else is `400` (`YouTube trailers are either true or false.`). The parent screen has a checkbox for this, shown only while a parent is signed in. While the saved value is loading, the checkbox is not shown. If it cannot be loaded, the screen shows an error with a retry button and no checkbox, so a stored `true` is never overwritten by a default.

When the setting is off, `trailerKey` is `null`, the videos endpoint is not called, the screen renders no trailer control, and no YouTube script is loaded. The parent approval queue uses the same trailer choice and the same player. When the setting is off, the queue does not ask TMDB for videos and does not show a play button.

When the setting is on, the screen still does not contact YouTube until the kid presses play. There is no iframe and no YouTube thumbnail before that click. Playback uses the [IFrame Player API](https://developers.google.com/youtube/iframe_api_reference):

- The loader is `https://www.youtube.com/iframe_api`. `https://www.youtube-nocookie.com/iframe_api` is not that script (it answers 404). If the script fails to load, the failed `<script>` tag is removed, the screen shows a short message and the kid can press play again to retry. If `YT.Player` is still not defined 10 seconds after the click (for example when the loader arrives but the widget script it pulls in fails), the `iframe_api` and widget `<script>` tags are removed, the screen shows the same message, focus returns to Play, and the next press loads fresh scripts. The page implements `onYouTubeIframeAPIReady` by calling any callback that was already set and restoring it when the trailer closes, so two players loading at once do not overwrite each other.
- The `YT.Player` constructor documents only `width`, `height`, `videoId`, `playerVars` and `events`; there is no documented `host` option, so none is used. On click the page creates the iframe itself, with `src` set to `https://www.youtube-nocookie.com/embed/{id}?enablejsapi=1&rel=0&iv_load_policy=3&playsinline=1&origin={page origin, URL-encoded}` (the [privacy-enhanced embed](https://support.google.com/youtube/answer/171780)), and attaches the player with `new YT.Player(iframe, { events })`, the pattern the [IFrame Player API](https://developers.google.com/youtube/iframe_api_reference) describes for an existing iframe. `enablejsapi=1` and `origin` are what that page requires for API control.
- Parameters, from the [player parameters](https://developers.google.com/youtube/player_parameters): `rel=0`, `iv_load_policy=3`, `playsinline=1`, `enablejsapi=1` and `origin` set to the page origin. `rel=0` only limits related videos to the same channel; it does not turn them off. `modestbranding` has had no effect since 15 August 2023 and is not sent. `autoplay`, `list`, `playlist` and `widget_referrer` are not set; the iframe allows `autoplay` and the page calls `playVideo()` from `onReady`, but the script loads after the click, so the browser (Safari and iOS in particular) may still block it. The player reports that with `onAutoplayBlocked`; the page then shows "Tap play to start the trailer." under the player and leaves YouTube's own play overlay in place for the kid to use. The hint clears when the trailer closes.
- The player fills the width of its container at a 16:9 aspect ratio, so on a viewport narrower than 480 pixels it is smaller than 480 by 270. The iframe has no `width` or `height` attributes because the stylesheet sizes it. Keyboard controls stay on (the default) so a remote can use them.
- When the player reports ended (`0`, `YT.PlayerState.ENDED`), the page calls `destroy()`, which removes the iframe, so the related-video end screen does not stay up. Close and Escape do the same. A player error destroys it and shows a short message. Closing while the script is still loading does not create the player afterwards.
- Focus moves to the Close button when the trailer opens, and returns to the Play button when it closes by any path (Close, Escape, the video ending, an error or a failed load). Close comes after the player in tab order, so Tab from the player always reaches it.
- Escape is handled by a listener on the page. While the YouTube iframe itself has focus, key presses go to the cross-origin frame and the page never sees them, so Escape does nothing there. Tab to Close, or click it.

The app does not send a Content-Security-Policy. An operator who adds one should allow `script-src https://www.youtube.com` (the loader and the widget script it pulls in) and `frame-src https://www.youtube-nocookie.com`, and should not allow `i.ytimg.com` or `img.youtube.com`. Do not set `Referrer-Policy` to `no-referrer`: an embed without a Referer fails with player error 153. A site that is directed at children has to self-designate that with YouTube; this app does not do it.
