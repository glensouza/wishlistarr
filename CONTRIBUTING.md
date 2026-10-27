# Contributing to Wishlistarr

Thanks for helping out. Wishlistarr is a kid-friendly request app for Plex, so changes are held to a high bar for safety and privacy: a kid session must never be able to reach a parent action, and the app must not collect telemetry.

## Getting started

You need the .NET SDK 10 and Node.js 24. The README explains how to [run locally](README.md#run-locally).

1. Fork the repository and create a branch from `main`.
2. Make your change, with tests.
3. Run the checks below.
4. Open a pull request against `main`.

## Optional dev AppHost

`dotnet run --project src/Wishlistarr.AppHost` starts the API and the Vite dev server together and opens the Aspire dashboard with their logs. It is optional: the API and `npm run dev` work on their own, and the AppHost is never referenced by the API or shipped in a release. Run `npm ci` in `web` first.

## Branch names

Use a short prefix and a descriptive slug: `feat/plex-label-writer`, `fix/session-expiry`, `docs/setup-guide`, `chore/update-dependencies`.

The prefix sets the release notes category: a workflow labels the pull request `enhancement` for `feat/`, `bug` for `fix/`, `documentation` for `docs/` and `area:devops` for `chore/`. See [docs/releasing.md](docs/releasing.md).

## Issues and labels

Issues get an `area:` label during triage (`area:backend`, `area:frontend`, `area:security`, `area:integration`, `area:docs`, `area:devops`, `area:testing`). Pull requests are labeled by the workflow described above, plus any `area:` label a maintainer adds. New reports start with `needs-triage` until a maintainer reviews them. Issues suited to newcomers are marked `good first issue`, and `help wanted` marks work that is open to anyone. Find them under [good first issue](https://github.com/glensouza/wishlistarr/labels/good%20first%20issue).

## Checks

Pull requests must pass CI, which runs the same commands you can run locally:

```sh
dotnet build
dotnet test
dotnet format --verify-no-changes
cd web
npm run lint
npm run format:check
npm run build
npm test
```

## Code style

C# style is defined in `.editorconfig` and enforced by the build; see [Code style](README.md#code-style) in the README. In both C# and TypeScript:

- Comments only explain a non-obvious why. Do not leave commented-out code or TODO markers.
- Follow the patterns of the surrounding code before introducing new ones.
- Add or update tests for the behavior you change.

## Conventions

These are the patterns the existing code follows. When in doubt, copy the nearest example.

### Project layout

- `src/Wishlistarr.Core` holds pure logic with no I/O, such as the content policy engine.
- `src/Wishlistarr.Data` holds the EF Core model and migrations.
- `src/Wishlistarr.Integrations` holds one client per outside service (Plex, TMDB, Seerr, Sonarr and Radarr).
- `src/Wishlistarr.Api` holds the endpoints and the services behind them, grouped by area.
- `tests/Wishlistarr.Tests` mirrors those areas in folders. `web/src` holds the React app, with kid screens in `web/src/kid`.

### API

- Endpoints live in `Map...` extension methods for their area. Give each one `WithName`, `WithSummary` and `Produces`.
- Return errors with `Results.Problem` and a plain-language title.
- Parent endpoints go in the parent group (`RequireParent`), and kid endpoints in the kid groups. Never put a parent action where a kid session can reach it. `AuthorizationBoundaryTests` walks every route, so a new route must be covered by it.
- A decision that kids see and one that parents preview must come from the same code path, so the two can never disagree.

### Outside services

- Put the client in `Wishlistarr.Integrations`, and write down how the service behaves in `docs/`.
- Test fakes must follow what the real service does (status codes, error shapes, public versus authenticated endpoints), taken from its documentation or source. A fake written only to agree with the code proves nothing.

### Database

- Follow [docs/migrations.md](docs/migrations.md).
- Store enums as strings (`HasConversion<string>()`), like the existing columns.
- Load related rows explicitly where they are needed instead of loading them on every query.
- TMDB data is kept for at most 6 months; see [docs/tmdb.md](docs/tmdb.md) before storing any new TMDB field.

### Frontend

- Every user-visible string comes from the catalog in `web/src/i18n/en.ts`. A test fails when code uses a key that is not in the catalog.
- Call the API through the helpers in `web/src/api.ts`.
- Controls need labels and roles, work with the keyboard, and fit a phone screen. Kid screens must also work with arrow keys and Enter.
- Show specific, plain-language error messages instead of one generic failure message.

### Tests

- API tests use the shared test host in `tests/Wishlistarr.Tests` and its fakes. Use `ManualTimeProvider` for anything that depends on time.
- Write tests that fail if the behavior is removed. Assert the result a user would see, not the internal calls.
- Cover failure paths (an outside service being down, invalid input, the wrong kind of session) as well as the happy path.

### Docs

Update the matching page in `docs/` when you change behavior that it describes.

## Releases

Maintainers cut releases by tagging `main`; see [docs/releasing.md](docs/releasing.md).

## Pull requests

- Keep a pull request to one logical change.
- Use a short, imperative title, such as "Add Plex label writer".
- Describe what changed, why, and how you tested it.
- Commit messages are short, imperative and neutral.
- Link the issue it closes, if any.

`main` is protected: changes land through pull requests with passing checks.

## Security and privacy

- Do not report vulnerabilities in a public issue. Use GitHub's private vulnerability reporting on the repository's Security tab.
- Authentication and authorization changes need extra care; read [docs/authentication.md](docs/authentication.md) first.
- Do not add telemetry, analytics or third-party scripts.
- Never commit secrets, API keys or real media-server data. Use demo data in tests and screenshots.

## License

By contributing you agree that your contributions are licensed under the GNU General Public License v3.0, the same as the project. See [LICENSE](LICENSE).
