# Attribution

Wishlistarr shows these credits on the in-app About page (`/about`). That page is linked from the footer on every screen, including kid mode, and from the parent area. It is not a parent route and it does not read parent data. The TMDB notice below is the same one already on the project site and in the README.

## TMDB

[![TMDB](../site/tmdb.svg)](https://www.themoviedb.org/)

This product uses the TMDB API but is not endorsed or certified by TMDB.

The logo is TMDB's alt short logo, stored at `site/tmdb.svg`. It links to <https://www.themoviedb.org/>. Do not recolor it, change its aspect ratio, or draw it larger than the Wishlistarr name. On the About page its width is `7.5rem`.

TMDB's developer FAQ asks for the sentence above. The API terms of use use a longer template ("uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB"). Wishlistarr shows the FAQ sentence, the notice already on the site and in the README, and does not show a second wording.

Refer to the service as TMDB or The Movie Database.

## JustWatch

Watch-provider results from TMDB are supplied by JustWatch. TMDB requires the source to be attributed as JustWatch on each item that shows that data. `WatchProviders` is the component that renders provider names, and it always includes `WatchProviderCredit`: the text "Watch provider data from JustWatch" and a link to <https://www.justwatch.com/>. A screen that shows provider data has to use that component. An empty list, or a list whose names are blank, renders nothing, so the credit is not shown when that screen has no provider data.

The About page lists the same credit. It is text. Wishlistarr does not ship the JustWatch logo or streaming-service logos.

## fanart.tv

Artwork data from [fanart.tv](https://fanart.tv/). Text only.

## DoesTheDogDie

Powered by [DoesTheDogDie.com](https://www.doesthedogdie.com). The visible words are that phrase, and the link target is `https://www.doesthedogdie.com`. Text only.

## Plex

Wishlistarr is not affiliated with, endorsed, or sponsored by Plex, Inc. Plex is a trademark of Plex, Inc.

Wishlistarr does not ship the Plex logo.

## Open source licenses

Wishlistarr is free software under the GNU General Public License v3.0. The About page also lists third-party packages from two files the build writes:

- `web/public/licenses/npm.json`, from `license-checker-rseidelsohn`, for production npm packages
- `web/public/licenses/nuget.json`, from `nuget-license`, for the NuGet packages shipped by the Api, Core, Data, and Integrations projects

Neither file is committed. `npm run build` and `npm run dev` write the npm file before Vite runs. `dotnet build` writes the NuGet file and copies it to `src/Wishlistarr.Api/wwwroot/licenses/nuget.json`. Vite copies `web/public` into that wwwroot, so a web build after a .NET build keeps both files. The Docker image runs the web build and then `dotnet publish`, which runs the NuGet check again and publishes `wwwroot/licenses/nuget.json` with the static files. The About page fetches `/licenses/npm.json` and `/licenses/nuget.json`.

Allowed SPDX licenses are in `build/allowed-licenses.json`. An unknown license, or a license that cannot be shipped with a GPL-3.0 program, fails `dotnet build` or `npm run build` and names the package. An `OR` expression is accepted when any side is allowed. An `AND` expression is accepted only when every side is allowed. A `WITH` expression is accepted only when the whole `ID WITH exception` text is listed. Guessed licenses (a `*`), `UNKNOWN`, `UNLICENSED`, and anything the check cannot parse are rejected.

If either notice file is missing, empty, not JSON, or incomplete, the About page says the package license list is unavailable and does not show a partial list. A missing notice file is a 404: the SPA fallback does not serve a path that looks like a file. The page also ignores an HTML body, so the site shell is not treated as a license list.

`Microsoft.EntityFrameworkCore.Design` is design-time only (`Publish` is false) and is left out of the NuGet scan, with the packages that only it brings in.

CI uploads both files as artifacts. The release workflow writes them before the image build, uploads those artifacts, and attaches `npm-licenses.json` and `nuget-licenses.json` to the GitHub release. See [Releasing](releasing.md).
