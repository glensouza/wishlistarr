# Kid profiles

A kid profile has a name, an avatar, an optional PIN, optional age limits for movies and TV, a Plex label, an optional Plex user id and an optional weekly request allowance. Parents manage profiles in the web app or through the API; kids pick their profile and enter its PIN to get a Kid session.

## Managing profiles

All of these require the Parent role.

- `GET /api/v1/kid-profiles` lists active profiles. PIN hashes are never returned, only `hasPin`.
- `POST /api/v1/kid-profiles` and `PUT /api/v1/kid-profiles/{id}` take `{ name, avatar, pin, removePin, movieAgeLimit, tvAgeLimit, plexLabel, plexUserId, weeklyAllowance }`. `PUT` replaces the profile; leave `pin` out to keep the current PIN, or send `removePin: true` to clear it.
- `DELETE /api/v1/kid-profiles/{id}?removeOwnedLabels=true|false` archives the profile.
- `POST /api/v1/kid-profiles/{id}/unlock` unlocks a locked profile and clears its failed PIN counts.

The web form picks the Plex user from `GET /api/v1/plex/users` (or None); if Plex is not connected the picker says so and the profile can still be saved without a mapping. `plexUserId` is stored as given, up to 100 characters, and saving never calls Plex.

Names are 1 to 50 characters and unique among active profiles (case-insensitive). Avatars are 1 to 32 lowercase letters, digits or dashes. Age limits are 0 to 21 and a missing limit means no rating limit; titles without a usable rating still follow the unrated policy (see [content policy](content-policy.md)). Weekly allowances are 0 to 1000.

### Plex label

The label defaults to the profile name. Labels are 1 to 64 characters, cannot start or end with whitespace, and cannot contain commas, pipes or control characters, because Plex content filters use those to separate values. Each active profile owns a distinct label.

### PINs

PINs are 4 to 8 digits. They are stored as salted PBKDF2 hashes (the ASP.NET Core Identity password hasher, which compares in constant time) and are never returned or logged. A PIN is required for every profile as soon as the active profiles do not all share the same movie and TV limits; saving a profile that would break this is rejected with the names that still need a PIN. Set `Kids:RequirePinWhenLimitsDiffer` to `false` to turn the rule off.

### Deleting

Deleting archives the profile instead of removing it: the row stays so request history can keep pointing at it, its PIN, Plex user mapping and sessions are cleared, and its name and label become available again. `removeOwnedLabels` is recorded on the archived profile (`RemoveOwnedLabelsOnArchive`) for the Plex label writer to act on later; nothing is removed from Plex yet.

## Signing in

- `GET /api/v1/kid/profiles` is anonymous and returns `{ id, name, avatar, requiresPin }` for each active profile, for the picker.
- `POST /api/v1/kid/signin` is anonymous and takes `{ profileId, pin }`. Success returns the picker entry and sets the `wishlistarr.kid` cookie (HttpOnly, `SameSite=Strict`). The session is stored in the database and lasts 7 days (`Kids:SessionLifetime`).
- `GET /api/v1/kid/me` and `POST /api/v1/kid/signout` need a Kid session. Kid sessions are rejected on every parent endpoint. Writes made with a Kid cookie, including signing in again from a signed-in device, need the antiforgery token like parent writes.

Unknown, archived and wrong-PIN attempts all answer `401 Invalid PIN.` with the same body, and an unknown profile still costs a password hash. Sign-in shares the per-address rate limit used by parent login.

### Backoff and lockout

Each device gets a random `wishlistarr.device` cookie, and failed PINs are counted per device and profile. After the n-th consecutive failure the device must wait `2 s * 2^(n-1)` (`Kids:BackoffBase`), capped at 15 minutes (`Kids:BackoffMax`). Attempts during the wait get `429` with `Retry-After`, even with the right PIN. Counts are forgotten after an hour without failures (`Kids:FailureResetAfter`) and on a successful sign-in.

A device that clears its cookies looks new, so failures are also counted across all devices for the profile. After 10 (`Kids:LockThreshold`) the profile is locked: sign-in answers `423` and existing Kid sessions stop working until a parent unlocks it.

A profile that never existed is not subject to backoff, so repeated attempts can tell an id that was never used from one that has been. Only ids from the picker, or ones that were once valid, are worth testing.

### Parent notifications

After 5 consecutive failures on one device (`Kids:NotifyThreshold`) and when a profile locks, Wishlistarr calls `IParentNotifier.NotifyAsync` with a `ParentNotification(Kind, Message)`; the kinds are `kid.pin.failures` and `kid.profile.locked`. The only implementation for now writes a warning to the log. Messages contain the profile name and counts, never a PIN. Add another `IParentNotifier` registration to deliver them elsewhere.
