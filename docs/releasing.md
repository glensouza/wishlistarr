# Releasing

Releases are cut by pushing a `v`-prefixed SemVer tag. The release workflow builds the image, publishes it to `ghcr.io/glensouza/wishlistarr` and creates the GitHub release.

## Versioning

Tags follow [Semantic Versioning](https://semver.org/) with a `v` prefix, for example `v1.2.3`.

- Increase the major version for incompatible changes, including database changes that cannot be rolled back.
- Increase the minor version for backward-compatible features.
- Increase the patch version for backward-compatible fixes.
- A suffix after a hyphen, such as `v1.0.0-rc.1`, marks a pre-release.

## Cut a release

Tags must be cut from commits on `main`; the workflow fails otherwise.

1. Make sure `main` is green in CI and contains everything for the release.
2. Create and push the tag from `main`:

   ```sh
   git checkout main
   git pull
   git tag v1.2.3
   git push origin v1.2.3
   ```

3. Watch the **Release** workflow. It builds `linux/amd64` and `linux/arm64`, pushes the image tags below, then runs `gh release create --generate-notes`. The release title is the tag name. The notes are the merged pull requests since the previous release, grouped by `.github/release.yml`.

## Release notes

GitHub writes the release notes. The workflow does not install changelog software and does not need a token other than the `GITHUB_TOKEN` it already uses to publish the image and the release.

`gh release create --generate-notes` asks GitHub to list pull requests merged since the previous release. GitHub reads `.github/release.yml` from the repository. If that file is missing, GitHub uses its default notes instead, so the workflow fails the release step when the tagged commit does not contain `.github/release.yml`. Creating the release by hand uses the same file: `gh release create v1.2.3 --title v1.2.3 --generate-notes`.

Pull requests are labeled from their branch name by the **Label pull requests** workflow, which uses [actions/labeler](https://github.com/actions/labeler) and the rules in `.github/labeler.yml`. The labels must already exist; the workflow does not create them. Each category in `.github/release.yml` lists labels that exist on the repository:

| Branch prefix | Label applied | Category |
| --- | --- | --- |
| `feat/` | `enhancement` | Features |
| `fix/` | `bug` | Fixes |
| `docs/` | `documentation` | Documentation |
| `chore/` | `area:devops` | Maintenance |

| Category | Labels |
| --- | --- |
| Security | `area:security` |
| Features | `enhancement` |
| Fixes | `bug` |
| Documentation | `documentation`, `area:docs` |
| Maintenance | `area:devops`, `area:testing`, `dependencies` |
| Other changes | anything else |

Applied by hand: `area:security`, `area:docs`, `area:testing`, `dependencies`.

Add one of those labels to a pull request when its branch prefix does not tell the whole story, for example `area:security` on a `fix/` branch. `area:backend`, `area:frontend` and `area:integration` do not pick a category, so a pull request that only has one of those labels is listed under Other changes. Pull requests opened by `dependabot` are left out of the notes.

The labeler runs on `pull_request_target`, because the token of a `pull_request` run on a pull request from a fork is read-only and cannot add labels. GitHub warns that this trigger can hand repository write access to a malicious pull request author if the workflow runs untrusted code. This workflow has no checkout step and runs only the pinned action, which reads `.github/labeler.yml` from the base branch, so nothing from the pull request is executed. The token is limited to `contents: read` and `pull-requests: write`. Labels are added when a pull request is opened or reopened and are never removed by the workflow. Because the trigger uses the base branch, a pull request that changes the labeler or its rules is not labeled by its own change.

## Image tags

| Release | Tags published |
| --- | --- |
| Stable, `v1.2.3` | `1.2.3`, `1.2`, `1`, `latest` |
| Pre-release, `v1.0.0-rc.1` | `1.0.0-rc.1` only |

Publishing a stable patch on an older line, such as `v1.1.5` after `v1.2.0`, would still move `latest` and `1`, so avoid it. Pre-releases never move `latest`, `1` or `1.2`, and the GitHub release is marked as a pre-release. Stable releases are marked as the latest release.

## Version in the app

The workflow passes the tag without its `v` as the `VERSION` build argument, which the Dockerfile hands to `dotnet publish` as the MSBuild `Version`. The app reports it as the assembly informational version, for example at `GET /api/v1/system/status`, which requires a parent sign-in or the API key. An image built without the `VERSION` build argument reports `0.0.0`; `dotnet run` reports the default assembly version.

To build an image yourself:

```sh
docker build --build-arg VERSION=1.2.3 -t wishlistarr:1.2.3 .
```

## Pull a version

```sh
docker pull ghcr.io/glensouza/wishlistarr:1.2.3
docker pull --platform linux/arm64 ghcr.io/glensouza/wishlistarr:1.2.3
```

The tags are multi-arch manifests, so Docker selects the matching architecture automatically. Pin an exact version in production and use `1.2` or `1` to follow patch or minor updates.

## If a release fails

- If the workflow fails before the image is pushed, fix the cause and re-run the failed workflow from the Actions page.
- If the image was pushed but the GitHub release step failed, re-run the failed job; or create the release manually with `gh release create v1.2.3 --title v1.2.3 --generate-notes`. That command uses `.github/release.yml` the same way the workflow does.
- If the tag itself is wrong and nothing was published, delete it with `git push --delete origin v1.2.3` and `git tag -d v1.2.3`, then tag again.

## Withdraw a release

Published versions should not be rewritten. To withdraw a bad release:

1. Delete or edit the GitHub release, and note the problem in its description.
2. Delete the affected version in the package settings on GitHub, if it must not be pulled.
3. If it was the latest stable release, publish a fixed patch release, which moves `latest`, `1` and `1.2` forward.
