<p align="center">
  <img src="site/logo.svg" alt="Wishlistarr" width="128">
</p>

# Wishlistarr

[![CI](https://github.com/glensouza/wishlistarr/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/glensouza/wishlistarr/actions/workflows/ci.yml)
[![Release](https://github.com/glensouza/wishlistarr/actions/workflows/release.yml/badge.svg)](https://github.com/glensouza/wishlistarr/actions/workflows/release.yml)
[![Pages](https://github.com/glensouza/wishlistarr/actions/workflows/pages.yml/badge.svg?branch=main)](https://github.com/glensouza/wishlistarr/actions/workflows/pages.yml)
[![Sponsor](https://img.shields.io/badge/Sponsor-glensouza-ea4aaa?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/glensouza)

Seerr, but for kids. They wish, you approve, and Plex stays safe.

Wishlistarr is a free and open source request app for families that share a Plex server. Each child gets their own profile with a PIN, browses only titles that fit the rules you set, and asks for what they want to watch. You decide what is allowed outright, what needs your approval and what is never shown.

- **Kid profiles:** a profile picker and PIN sign-in, with a child-friendly interface.
- **Content policy:** rules per profile decide whether a title is allowed, needs approval or is blocked.
- **Parent approval:** requests that need a decision wait for a parent before anything is downloaded.
- **Plex labels:** approved titles are labeled for the profile so Plex restrictions keep each child to their own library.
- **Private by design:** nothing is sent as telemetry. An optional local metrics endpoint stays off unless you enable it. Metadata comes from TMDB.

Wishlistarr is under active development and not yet released.

## Docker

```sh
cp docker-compose.example.yml compose.yaml
docker compose up -d
```

The image is published as `ghcr.io/glensouza/wishlistarr` for `linux/amd64` and `linux/arm64`. See [docker-compose.example.yml](docker-compose.example.yml).

| Setting | Default | Purpose |
| --- | --- | --- |
| `PUID` / `PGID` | `1000` / `1000` | User and group the app runs as. The data volume is chowned to them at start. Must not be `0`; the app never runs as root. |
| `TZ` | `UTC` | Time zone, for example `America/Los_Angeles`. |
| `/data` | | Volume holding the database, keys, backups and logs. See [docs/backup.md](docs/backup.md). |
| `8080` | | Port the web interface and API listen on. |

The container must start as root (do not set `--user`, `user:` or `runAsNonRoot`): the entrypoint applies `PUID`/`PGID` and then drops privileges, so the app itself never runs as root.

The container healthcheck calls `GET /api/health`.

Tags: `1.2.3`, `1.2`, `1` and `latest` for stable releases; pre-releases such as `1.0.0-rc.1` only get their exact tag. See [docs/releasing.md](docs/releasing.md).

## Requirements

- .NET SDK 10
- Node.js 24

## Run locally

Build the frontend into the API's `wwwroot`, then start the API:

```sh
cd web
npm ci
npm run build
cd ..
dotnet run --project src/Wishlistarr.Api
```

The API serves the built frontend and exposes `GET /api/health`.

For frontend development, run the API and then `npm run dev` in `web`; the Vite dev server proxies `/api` to `http://localhost:5082`.

Optionally, start both with one command and view logs in the Aspire dashboard:

```sh
cd web
npm ci
cd ..
dotnet run --project src/Wishlistarr.AppHost
```

The AppHost is a contributor tool only. It is not needed to run or build Wishlistarr, and it is not part of any release. Vite reads the API address from `WISHLISTARR_API_URL`, which the AppHost sets; it defaults to `http://localhost:5082`.

On first start a parent account must be created before the API can be used. See [docs/authentication.md](docs/authentication.md) for sign-in, sessions, the API key and the optional trusted-network bypass. See [docs/kid-profiles.md](docs/kid-profiles.md) for kid profiles and PIN sign-in, and [docs/kid-devices.md](docs/kid-devices.md) for pairing kid devices. See [docs/plex.md](docs/plex.md) to link a Plex account and choose a server. See [docs/requests.md](docs/requests.md) for how requests move from pending to labeled. See [docs/kid-search.md](docs/kid-search.md) for kid search and its latency budget. See [docs/kid-home.md](docs/kid-home.md) for the kid home rows. See [docs/title-details.md](docs/title-details. See [docs/webhooks.md](docs/webhooks.md) for inbound Plex, Sonarr, Radarr and Seerr webhooks. See [docs/library.md](docs/library.md) for the local Plex library index. See [docs/backup.md](docs/backup.md) for backups, retention and restore. See [docs/health.md](docs/health.md) for health checks, the log viewer, request history and the optional metrics endpoint.

See [docs/notifications.md](docs/notifications.md) for parent notification providers, delivery retries, and signed approval links.

## Test

```sh
dotnet build
dotnet test
cd web
npm run lint
npm run format:check
npm run build
npm test
```

## Code style

C# style is defined in `.editorconfig` and enforced as warnings (errors in the build): explicit types instead of `var`, `this.` on instance members, target-typed `new()`, collection expressions, file-scoped namespaces, usings outside the namespace with `System` first, and camelCase private fields without an underscore. Prefer primary constructors and sealed classes where they fit, and give public async methods a trailing `CancellationToken ct = default`. Run `dotnet format --verify-no-changes` before pushing.

## Privacy

Wishlistarr does not send telemetry or analytics. `GET /metrics` is a local Prometheus scrape and it stays off unless `Metrics:Enabled` is true. See [docs/health.md](docs/health.md).

## Acknowledgements

<a href="https://www.themoviedb.org/"><img src="site/tmdb.svg" alt="TMDB" width="140"></a>

This product uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB.

Watch provider data comes from [JustWatch](https://www.justwatch.com/). Artwork data comes from [fanart.tv](https://fanart.tv/). Powered by [DoesTheDogDie.com](https://www.doesthedogdie.com).

Wishlistarr is not affiliated with, endorsed, or sponsored by Plex, Inc. Plex is a trademark of Plex, Inc.

The same credits, and the open source package list, are on the in-app About page for parents and in [docs/attribution.md](docs/attribution.md).

## License

Copyright (C) Glen Souza. Licensed under the GNU General Public License v3.0; see [LICENSE](LICENSE).
