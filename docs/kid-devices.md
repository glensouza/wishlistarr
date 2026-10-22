# Kid devices

Kids sign in on devices a parent has paired. A browser that has not been paired only shows the parent sign-in and a box for a pairing code; the profile picker, kid sign-in and the way out of kid mode all need a paired device. Each device shows only the profiles a parent assigned to it.

## Pairing a device

1. As a parent, open **Parents**, then **Kid devices**, and choose **Add device**. Give it a name (1 to 50 characters) and tick the profiles it should show.
2. Wishlistarr shows an eight character pairing code such as `K7QX-M2PD`. It is shown once, works once and expires after 10 minutes (`Kids:PairingCodeLifetime`, at most one hour).
3. On the kid's device, open Wishlistarr, choose **Parents** and enter the code under **Use this device for kids**. The browser now shows the profile picker.

Codes use the digits and the letters other than I, L, O and U, so they are easy to read aloud. Case, spaces and dashes are ignored, and O, I and L are read as 0, 1 and 1. Issuing a new code for a device replaces its earlier one. Redeeming a code for a device that was already paired replaces its token, so the old browser stops working and its kid sessions end.

## The device token

Pairing sets the `wishlistarr.device` cookie (HttpOnly, `SameSite=Strict`, `Secure` over HTTPS). It lasts a year (`Kids:DeviceCookieLifetime`, at most 400 days, the longest browsers keep a cookie) and is renewed each time a kid signs in, so a device in regular use stays paired. Clearing the browser's cookies unpairs it.

The token is 32 random bytes from the operating system's cryptographic generator. Only its SHA-256 hash is stored and it is compared in constant time; pairing codes are stored the same way. Neither is ever returned by the API or written to the logs; the code appears only in the response that creates it and the token only in the cookie.

Redeeming a code is a single delete of the code's row inside a transaction. When two browsers send the same code at the same moment only one delete removes the row, and only that browser is paired.

## Managing devices

All of these require the Parent role.

- `GET /api/v1/kid-devices` lists devices with `{ id, name, profileIds, isPaired, pairingCodeExpiresAt, createdAt, pairedAt, lastSeenAt, isCurrent }`. `isCurrent` marks the device the request came from.
- `POST /api/v1/kid-devices` takes `{ name, profileIds }` and returns `201` with `{ device, code, expiresAt }`.
- `PUT /api/v1/kid-devices/{id}` takes `{ name, profileIds }` and replaces the name and the assigned profiles. Kid sessions on that device for profiles that were removed end at once.
- `POST /api/v1/kid-devices/{id}/pairing-code` returns a new `{ code, expiresAt }` for the device.
- `DELETE /api/v1/kid-devices/{id}` revokes the device. Its token stops working on the next request, kid sessions on it end, and its PIN failure counts are removed.

Only active profiles can be assigned. Archiving a profile removes it from every device.

## Kid endpoints

- `POST /api/v1/kid/pair` is anonymous and takes `{ code }`. It shares the per-address rate limit used by parent login. A wrong, used or expired code answers `400` with the same message.
- `GET /api/v1/kid/profiles`, `POST /api/v1/kid/signin` and `POST /api/v1/kid/exit` need a paired device (the `KidDevice` policy). Without one they answer `401`, or `403` to a signed-in caller.
- `GET /api/v1/auth/status` includes `kidDevice: { name }` when the browser is paired, and `null` otherwise.

A kid session belongs to the device it started on. It ends when the device is revoked or re-paired, when its profile is removed from the device, and it is not accepted from a browser that does not carry that device's cookie.

## Leaving kid mode

On a paired device the **Parents** link leads to **Leave kid mode**, which asks for the parent's username and password. `POST /api/v1/kid/exit` takes `{ username, password }`, checks them the same way as parent login (including the account lockout), ends the kid session on the device and starts a parent session. A wrong password leaves the kid session as it was. While a kid is signed in the request needs the antiforgery token, like other kid writes.

Paired devices never get the trusted-network bypass. A request that carries the device cookie is not treated as a parent even when it comes from a trusted network, so the password is needed there too.

The device stays paired. When the parent chooses **Return to kid mode** the parent session ends and the device shows the profile picker again. A kid signing in on the device also ends any parent session left open in that browser. To stop using a device for kids altogether, revoke it under **Kid devices**.

## PIN limits per device

Wrong PINs are counted per paired device and profile. Backoff and the lock after `Kids:LockThreshold` failures apply only to that device, so failures on one device cannot lock a kid out on another. See [kid-profiles.md](kid-profiles.md#backoff-and-lockout).

With the defaults, someone guessing on one device can make 9 wrong guesses (about 17 minutes of backoff between them) before the tenth would lock the profile there. If they stop at 9 and wait for the count to reset after an hour without failures, they get roughly 9 guesses per device per hour. Guessing a 4 digit PIN that way takes about a month on average, and each extra digit multiplies that by ten. Only a parent can pair a device, so guessing cannot be spread across new devices. Parents get a `kid.pin.failures` notification after 5 failures in a row on a device.

## Upgrading

Browsers that were used for kid sign-in before this release are not paired. Kid sessions started before the upgrade stop working, and each kid device has to be paired once.
