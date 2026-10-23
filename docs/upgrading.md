# Upgrading

Replace the running image with the version you want, then start it again. Wishlistarr applies database migrations on startup. Migrations only move forward. To go back, restore a copy of the data directory and start the previous image. See [migrations.md](migrations.md) and [releasing.md](releasing.md).

## Steps

1. Copy the data directory (`WISHLISTARR_DATA_DIR`, or `data` next to the application). It holds the database and the `keys` folder.
2. Read the release notes for the version you are moving to.
3. Pull that image, point the service at its tag, and start it. Pin an exact tag in production, or follow a minor or major line, as described in [releasing.md](releasing.md).

```sh
docker pull ghcr.io/glensouza/wishlistarr:1.2.3
```

## Update notice

A signed-in parent sees a notice when a newer version has been published. The notice links to that version's release notes and to this page. Kid screens never show it, and the kid API does not include it.

The notice comes from a daily check of GitHub's list-releases API. That check is the only call Wishlistarr makes to GitHub by itself.

- The address is fixed: `https://api.github.com/repos/glensouza/wishlistarr/releases?per_page=100`. A setting or request cannot change it.
- The `User-Agent` is the app name and version only, such as `Wishlistarr/1.2.3`. The request has no token and no account, device or other identifier.
- The check runs at most once a day, including after a failure, and waits at most 10 seconds. Redirects are not followed. Responses over 5 MB are ignored.
- Being offline does not stop the app. A failed check is logged and ignored, and the previous result is kept.
- Drafts are ignored. Pre-releases are ignored unless the running version is itself a pre-release. Versions are compared as semantic versions, not by the order GitHub returns them.
- Only the first page is read, so at most 100 releases.

The check is on when `updates.check` is missing. Turn it off from the Parents page, with `PUT /api/v1/updates` and `{ "checkEnabled": false }`, or with `PUT /api/v1/settings/updates.check` and `{ "value": "false" }`. While it is off, the app does not call GitHub and does not show the notice. Those writes accept only `true` or `false`. A stored value that is not `true` leaves the check off.
