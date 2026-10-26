# Attribution

Wishlistarr shows full credits, with links to each service, on the in-app About page (`/about`). Only a signed-in parent can open it: the link is in the parent area, and anyone else who opens `/about` is sent to the parent sign-in. Kid mode never shows an outbound link. Every screen, kid screens included, ends with the TMDB logo and notice as plain text and an image, with no link and nothing else.

A test (`web/src/assets.test.ts`) fails if any source file other than the About page contains an absolute `http(s)` link, a third-party `src`, `window.open` or `target="_blank"`, and component tests render the profile picker, the kid home and a paired device and check that none of them has an outbound link or an About link.

Outbound links on the About page open in a new tab with `target="_blank"` and `rel="noreferrer noopener"`.

## TMDB

[![TMDB](../site/tmdb.svg)](https://www.themoviedb.org/)

This product uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB.

That is the notice in TMDB's API terms of use (<https://www.themoviedb.org/api-terms-of-use>) with "product" as the product name. The same sentence is on the project site, in the README and in the app. TMDB's developer FAQ shows a shorter sentence; the terms are the binding text, so the terms wording is used everywhere.

The logo is TMDB's alt short logo. The project site and README use `site/tmdb.svg`; the app has its own copy in `web/src/assets/tmdb.svg` so the web build does not read files outside `web/`. Do not recolor it or change its aspect ratio, and keep it less prominent than the Wishlistarr name. Its width is `7.5rem`. In the app it links to <https://www.themoviedb.org/> on the About page only.

Refer to the service as TMDB or The Movie Database.

## JustWatch

TMDB's watch provider data comes from JustWatch, and TMDB requires that the source is attributed as JustWatch (<https://developer.themoviedb.org/reference/movie-watch-providers>). `WatchProviders` is the component that renders provider names, and it always includes `WatchProviderCredit`, the text "Watch provider data from JustWatch". It is plain text so it is safe on kid screens. An empty list, or a list whose names are blank, renders nothing.

The rule: any component that renders watch provider data must render `WatchProviders` (or `WatchProviderCredit`). A test in `web/src/about/WatchProviders.test.tsx` scans every `.tsx` file and fails if one mentions provider fields (watch providers, `provider_name`, `provider_id`, `display_priority`, `logo_path`, `flatrate`, JustWatch) without using one of those components. Provider data is not shown on any screen yet.

The About page shows the same credit with a link to <https://www.justwatch.com/>. Wishlistarr does not ship the JustWatch logo or streaming-service logos.

## fanart.tv

Artwork data from [fanart.tv](https://fanart.tv/). Text only, on the About page.

## DoesTheDogDie

Powered by [DoesTheDogDie.com](https://www.doesthedogdie.com). The visible words are that phrase, and the link target is `https://www.doesthedogdie.com`. Text only, on the About page. It is a trigger-content site, which is why the link is not available in kid mode.

## Plex

Wishlistarr is not affiliated with, endorsed, or sponsored by Plex, Inc. Plex is a trademark of Plex, Inc.

Wishlistarr does not ship the Plex logo.

## Open source licenses

Wishlistarr is free software under the GNU General Public License v3.0. The About page also lists third-party packages from two files:

- `web/public/licenses/npm.json`, written by `web/scripts/npm-notices.mjs`, for production npm packages. The script reads `npm ls --omit=dev --all --json --long` and each package's `license` field, so it needs no extra dependency.
- `nuget.json`, written by `nuget-license` through `build/LicenseNotices.targets`, for the NuGet packages shipped by the Api, Core, Data, and Integrations projects.

Neither file is committed. `npm run build` and `npm run dev` write the npm file before Vite runs, and Vite copies it into `wwwroot`. The NuGet file is written by `dotnet publish` of the Api project, not by `dotnet build`, so test builds do not run the license tool. Publish writes the file to `src/Wishlistarr.Api/obj/licenses/nuget.json`, copies it to `web/public/licenses/nuget.json` when that folder exists (for the Vite dev server), and copies it into the publish output at `wwwroot/licenses/nuget.json`. It is never added to the publish item list, so a copy already in `wwwroot` cannot cause a duplicate-output error. The Docker image runs the web build and then `dotnet publish`, so every image has both files. The About page fetches `/licenses/npm.json` and `/licenses/nuget.json`. In local development `/licenses/nuget.json` exists only after a `dotnet publish` of the Api project, so the About page shows the NuGet list as unavailable until then.

The web stage of the Dockerfile copies `build/allowed-licenses.json` because the npm script reads it. A test (`web/scripts/npm-notices.test.ts`) fails when that stage does not copy every file listed in `repoInputs` in the script, and CI builds the Docker image without pushing it.

Allowed SPDX licenses are in `build/allowed-licenses.json`. An unknown license, or a license that cannot be shipped with a GPL-3.0 program, fails `dotnet publish` or `npm run build` and names the package. An `OR` expression is accepted when any side is allowed. An `AND` expression is accepted only when every side is allowed. A `WITH` expression is accepted only when the whole `ID WITH exception` text is listed. Guessed licenses (a `*`), `UNKNOWN`, `UNLICENSED`, and anything the check cannot parse are rejected. Tests cover this for npm (`web/scripts/license-policy.test.ts`) and for `nuget-license` with `OR` and `AND` expressions (`tests/Wishlistarr.Tests/LicenseNoticeTests.cs`, which runs the tool with a dead network proxy so it never needs the network, and does nothing when the tool package cannot be restored). The list is a policy for what may be distributed together with the program, not a statement that each license is GPL-compatible: OFL-1.1 is there for two bundled fonts, which ship as separate works, and a code package under it would also pass, so check the license of any new code dependency by hand.

If either notice file is missing, empty, not JSON, or incomplete, the About page says the package license list is unavailable and does not show a partial list. A missing notice file is a 404: the SPA fallback does not serve a path that looks like a file. The page also ignores an HTML body, so the site shell is not treated as a license list.

`Microsoft.EntityFrameworkCore.Design` is design-time only (`Publish` is false) and is left out of the NuGet scan, with the packages that only it brings in.

CI publishes the Api project, checks that `wwwroot/licenses/nuget.json` is in the output, and uploads both files as artifacts. The release workflow writes them before the image build, uploads those artifacts, and attaches `npm-licenses.json` and `nuget-licenses.json` to the GitHub release. See [Releasing](releasing.md).
