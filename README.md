<p align="center">
  <img src="site/logo.svg" alt="Wishlistarr" width="128">
</p>

# Wishlistarr

[![Sponsor](https://img.shields.io/badge/Sponsor-glensouza-ea4aaa?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/glensouza)

Seerr, but for kids. They wish, you approve, and Plex stays safe.

Wishlistarr is a free and open source request app for families that share a Plex server. Each child gets their own profile with a PIN, browses only titles that fit the rules you set, and asks for what they want to watch. You decide what is allowed outright, what needs your approval and what is never shown.

- **Kid profiles:** a profile picker and PIN sign-in, with a child-friendly interface.
- **Content policy:** rules per profile decide whether a title is allowed, needs approval or is blocked.
- **Parent approval:** requests that need a decision wait for a parent before anything is downloaded.
- **Plex labels:** approved titles are labeled for the profile so Plex restrictions keep each child to their own library.
- **Private by design:** no telemetry or analytics, and metadata comes from TMDB.

Wishlistarr is under active development and not yet released.

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

Optionally, start both with one command and view logs and traces in the Aspire dashboard:

```sh
cd web
npm ci
cd ..
dotnet run --project src/Wishlistarr.AppHost
```

The AppHost is a contributor tool only. It is not needed to run or build Wishlistarr, and it is not part of any release. Vite reads the API address from `WISHLISTARR_API_URL`, which the AppHost sets; it defaults to `http://localhost:5082`.

On first start a parent account must be created before the API can be used. See [docs/authentication.md](docs/authentication.md) for sign-in, sessions, the API key and the optional trusted-network bypass. See [docs/kid-profiles.md](docs/kid-profiles.md) for kid profiles and PIN sign-in. See [docs/plex.md](docs/plex.md) to link a Plex account and choose a server. See [docs/requests.md](docs/requests.md) for how requests move from pending to labeled. See [docs/kid-search.md](docs/kid-search.md) for kid search and its latency budget.

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

Wishlistarr does not collect any telemetry or analytics.

## Acknowledgements

<a href="https://www.themoviedb.org/"><img src="site/tmdb.svg" alt="TMDB" width="140"></a>

This product uses the TMDB API but is not endorsed or certified by TMDB.

## License

Copyright (C) Glen Souza. Licensed under the GNU General Public License v3.0; see [LICENSE](LICENSE).
