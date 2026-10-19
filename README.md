# Wishlistarr

[![Sponsor](https://img.shields.io/badge/Sponsor-glensouza-ea4aaa?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/glensouza)

A kid-friendly request app for Plex.

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

On first start a parent account must be created before the API can be used. See [docs/authentication.md](docs/authentication.md) for sign-in, sessions, the API key and the optional trusted-network bypass. See [docs/kid-profiles.md](docs/kid-profiles.md) for kid profiles and PIN sign-in. See [docs/plex.md](docs/plex.md) to link a Plex account and choose a server. See [docs/requests.md](docs/requests.md) for how requests move from pending to labeled.

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
