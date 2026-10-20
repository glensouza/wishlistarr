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

## Issues and labels

Issues carry an `area:` label (`area:backend`, `area:frontend`, `area:security`, `area:integration`, `area:docs`, `area:devops`, `area:testing`). New reports start with `needs-triage` until a maintainer reviews them. Issues suited to newcomers are marked `good first issue`, and `help wanted` marks work that is open to anyone. Find the newcomer issues at https://github.com/glensouza/wishlistarr/labels/good%20first%20issue.

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
