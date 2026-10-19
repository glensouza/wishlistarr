# Releasing

Releases are cut by pushing a `v`-prefixed SemVer tag. The release workflow builds the image, publishes it to `ghcr.io/glensouza/wishlistarr` and creates the GitHub release.

## Versioning

Tags follow [Semantic Versioning](https://semver.org/) with a `v` prefix, for example `v1.2.3`.

- Increase the major version for incompatible changes, including database changes that cannot be rolled back.
- Increase the minor version for backward-compatible features.
- Increase the patch version for backward-compatible fixes.
- A suffix after a hyphen, such as `v1.0.0-rc.1`, marks a pre-release.

## Cut a release

1. Make sure `main` is green in CI and contains everything for the release.
2. Create and push the tag from `main`:

   ```sh
   git checkout main
   git pull
   git tag v1.2.3
   git push origin v1.2.3
   ```

3. Watch the **Release** workflow. It builds `linux/amd64` and `linux/arm64`, pushes the image tags below, then runs `gh release create` with generated notes. The release title is the tag name.

## Image tags

| Release | Tags published |
| --- | --- |
| Stable, `v1.2.3` | `1.2.3`, `1.2`, `1`, `latest` |
| Pre-release, `v1.0.0-rc.1` | `1.0.0-rc.1` only |

Pre-releases never move `latest`, `1` or `1.2`, and the GitHub release is marked as a pre-release. Stable releases are marked as the latest release.

## Version in the app

The workflow passes the tag without its `v` as the `VERSION` build argument, which the Dockerfile hands to `dotnet publish` as the MSBuild `Version`. The app reports it as the assembly informational version, for example in the system status endpoint. Local builds report `0.0.0`.

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
- If the image was pushed but the GitHub release step failed, re-run the failed job; or create the release manually with `gh release create v1.2.3 --title v1.2.3 --generate-notes`.
- If the tag itself is wrong and nothing was published, delete it with `git push --delete origin v1.2.3` and `git tag -d v1.2.3`, then tag again.

## Withdraw a release

Published versions should not be rewritten. To withdraw a bad release:

1. Delete or edit the GitHub release, and note the problem in its description.
2. Delete the affected version in the package settings on GitHub, if it must not be pulled.
3. If it was the latest stable release, publish a fixed patch release, which moves `latest`, `1` and `1.2` forward.
