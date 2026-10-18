# Wishlistarr

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

For frontend development, run the API and then `npm run dev` in `web`; the Vite dev server proxies `/api` to `http://localhost:5000`.

## Test

```sh
dotnet build
dotnet test
cd web
npm run lint
npm test
```

## Privacy

Wishlistarr does not collect any telemetry or analytics.

## License

Copyright (C) Glen Souza. Licensed under the GNU General Public License v3.0; see [LICENSE](LICENSE).
