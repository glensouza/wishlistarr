# Plex restrictions

Wishlistarr tags each title with the kid profile's label (see [Plex labels](plex.md#labels)), but a label only limits what a kid sees once the Plex user mapped to the profile has a content filter that uses it. Plex stores that filter on the user, and Wishlistarr only reads it: it never changes a Plex user's restrictions.

## What is checked

For every active profile with a Plex user, Wishlistarr reads the movie and TV filters of that user from plex.tv (`/api/users`, the `filterMovies`, `filterTelevision` and `filterAll` attributes) and looks for the profile's label. The result is shown per type:

- `enforced`: an allow rule names the label (`mode` is `allowOnly`).
- `notEnforced`: the filter was read and no allow rule names the label. An exclude rule on the label counts as not enforced.
- `unknown`: Plex is not linked, plex.tv did not answer, the response has a shape Wishlistarr does not recognise, the user is not in the list, or the filter could not be understood. This is never an error and never produces a warning.

`GET /api/v1/kid-profiles` includes `plexRestrictions: { movies: { state, mode }, television: { state, mode } }` for each profile that has a Plex user, and `null` for profiles without one. The profile list in the web app shows the same. Each list request asks plex.tv once, however many profiles there are. The create and update responses carry `plexRestrictions: null`; only the list reports it.

`GET /api/v1/system/status` has a `warnings` list. A profile whose mapped user definitely does not enforce its label for movies, TV or both adds `{ code: "plex.label.not_enforced", message, profileId }`. The message names the types that are open. `GET /api/health` is unchanged and stays public, because the warnings name profiles.

## Allow-only and exclude

Plex filters are stored as `|` separated rules, for example `contentRating=TV-Y%2CTV-Y7|label=Ada`. Values are URL-encoded, and a comma separates values inside one rule.

- Allow only (`label=Ada`): titles carrying one of the listed labels are shared with the user. If no content ratings are set, only the labelled titles are shared, which is what Wishlistarr's labels are made for. If ratings are also set, Plex shares the labelled titles in addition to the titles matching the allowed ratings (see Plex's support article "Restricting the shares"), so unlabelled titles of those ratings stay visible. Leave the ratings empty if the label must be the only way a title becomes visible.
- Exclude (`label!=Ada`): titles carrying the label are hidden. Wishlistarr labels titles a kid should see, so excluding the profile's own label hides exactly those titles. Wishlistarr therefore reports an exclude rule on the profile's own label as not enforced, and an exclude rule on any other label is not enforcement of this label either.

## Fixing a profile that is not enforced

In Plex:

1. Open Settings and then Users and Sharing.
2. Edit the user that is mapped to the profile.
3. Open the user's restrictions, and for movies and for TV shows add the profile's label under Allow only. Do not use Exclude for it.
4. Save, then reload the profile list in Wishlistarr. Plex can take a moment to publish the change.

The exact wording of these screens differs between Plex versions and between Home and shared users. Restrictions can only be set for managed Home users and for friends the server is shared with; the server owner has none. Label restrictions need Plex Pass on the admin account. For a managed user, set the restriction profile to None first, otherwise the labels cannot be edited.

## Notes

- Writing restrictions to Plex is not implemented.
- Label matching is case-insensitive, like label writing.
- What was observed on a live plex.tv account: users with no restrictions return empty `filterMovies` and `filterTelevision` attributes, a restricted Home user returns `contentRating=...|label=...` in both, and the server owner is not in the list. The exclude form (`label!=`) was not available to observe on that account, so its syntax and the reading of it are assumed and unconfirmed against live data. The `filterAll` attribute was empty on every user observed, so how Plex uses it is also assumed: Wishlistarr treats a label in `filterAll` as applying to both movies and TV.
