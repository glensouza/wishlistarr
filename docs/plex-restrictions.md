# Plex restrictions

Wishlistarr tags each title with the kid profile's label (see [Plex labels](plex.md#labels)), but a label only limits what a kid sees once the Plex user mapped to the profile has a content filter that uses it. Plex stores that filter on the user, and Wishlistarr only reads it: it never changes a Plex user's restrictions.

## What is checked

For every active profile with a Plex user, Wishlistarr reads the movie and TV filters of that user from plex.tv (`/api/users`, the `filterMovies`, `filterTelevision` and `filterAll` attributes) and looks for the profile's label. The result is shown per type:

- `enforced`: the filter names the label. `mode` is `allowOnly` or `exclude`.
- `notEnforced`: the filter was read and does not name the label.
- `unknown`: Plex is not linked, plex.tv did not answer, the response has a shape Wishlistarr does not recognise, the user is not in the list, or the filter could not be understood. This is never an error and never produces a warning.

`GET /api/v1/kid-profiles` includes `plexRestrictions: { movies: { state, mode }, television: { state, mode } }` for each profile that has a Plex user, and `null` for profiles without one. The profile list in the web app shows the same. Each list request asks plex.tv once, however many profiles there are.

`GET /api/v1/system/status` has a `warnings` list. A profile whose mapped user definitely does not enforce its label for movies, TV or both adds `{ code: "plex.label.not_enforced", message, profileId }`. The message names the types that are open. `GET /api/health` is unchanged and stays public, because the warnings name profiles.

## Allow-only and exclude

Plex filters are stored as `|` separated rules, for example `contentRating=TV-Y%2CTV-Y7|label=Ada`. Values are URL-encoded, and a comma separates values inside one rule.

- Allow only (`label=Ada`): the user sees only titles that carry one of the listed labels. This is the mode Wishlistarr's labels are made for: label a title with the kid's label and it becomes visible to that user, and titles without the label stay hidden.
- Exclude (`label!=Ada`): the user sees everything except titles that carry one of the listed labels. Wishlistarr's labels are applied to titles a kid should see, so an exclude rule on the profile's own label would hide exactly those titles. Exclude is only useful with a label you apply yourself to titles that must stay hidden, and Wishlistarr reports it as enforced because Plex is filtering on the label.

If a user has both an allow rule and other rules such as a content rating, Plex combines them itself; Wishlistarr only checks that the label is present.

## Fixing a profile that is not enforced

In Plex:

1. Open Settings and then Users and Sharing.
2. Edit the user that is mapped to the profile.
3. Open the user's restrictions, and for movies and for TV shows add the profile's label to the allowed labels (or, if you want the opposite behaviour, to the excluded labels).
4. Save, then reload the profile list in Wishlistarr. Plex can take a moment to publish the change.

The exact wording of these screens differs between Plex versions and between Home and shared users. Restrictions can only be set for managed Home users and for friends the server is shared with; the server owner has none.

## Notes

- Writing restrictions to Plex is not implemented.
- Label matching is case-insensitive, like label writing.
- What was observed on a live plex.tv account: users with no restrictions return empty `filterMovies` and `filterTelevision` attributes, a restricted Home user returns `contentRating=...|label=...` in both, and the server owner is not in the list. The exclude form (`label!=`) was not available to observe on that account, so its syntax (`label!=`) is assumed from how Plex writes other negated filters and is not confirmed against live data.
