# chartarr for Lidarr

A Lidarr plugin that adds a **Chart CSV** import list: point it at a CSV
of albums (a RateYourMusic chart export, a best-of list, a spreadsheet)
and Lidarr imports them as monitored albums.

This is the plugin sibling of the [chartarr CLI](https://github.com/alperien/chartarr).
The CLI works with any Lidarr release and has an interactive review
screen; the plugin trades the review screen for full UI integration and
automatic re-syncing.

## Requirements

Plugins only exist on Lidarr's **nightly (plugins) branch** — they are
not available in stable releases. If you run stable Lidarr, use the
[CLI](https://github.com/alperien/chartarr) instead.

## Install

1. In Lidarr (nightly), open **System > Plugins**.
2. Paste `https://github.com/alperien/chartarr.plugin` and select Install.
3. Open **Settings > Import Lists**, add a new list, and pick
   **Chart CSV** under Other.

## Settings

- **CSV Path or URL** — a file path readable by Lidarr (mind docker
  volume mappings) or an http(s) URL. The file needs an artist column
  and a title/album column; anything else is ignored. RateYourMusic
  exports work as they are.
- **Use MusicBrainz matcher** — when on, each row is matched against
  MusicBrainz with the same scoring as the CLI (handles odd titles like
  "★ [Blackstar]", dual-script Japanese releases, and the
  album-vs-single ambiguity). Requests are paced to one per second, so
  the first sync of a big chart is slow — roughly half an hour for
  1,400 rows. Results are cached and later syncs are instant. When off,
  rows are passed to Lidarr by name and Lidarr's own mapper resolves
  them.
- **Match confidence (%)** — rows whose best match scores below this
  are passed to Lidarr by name instead of by MusicBrainz id. Title and
  artist similarity floors always apply on top. Threshold changes take
  effect on the next sync without new lookups.

Match results are cached in `chartarr-match-cache.json` inside Lidarr's
AppData folder; delete the file to force a full re-match. Failed
lookups are retried automatically after a week.

Monitoring, quality profile and root folder are configured on the
import list itself, like any other Lidarr list.

## Known limitations

- MusicBrainz and CSV-URL requests use a plain http client, so a proxy
  configured in Lidarr's settings is not applied to them yet.
- Uncertain matches have no review step here; use the CLI when you want
  to resolve them by hand.

## Building from source

```sh
sh setup.sh    # fetches the pinned Lidarr source into Submodules/Lidarr
dotnet build Chartarr/Chartarr.csproj -c Release -p:RunAnalyzers=false
dotnet test Chartarr.Tests/Chartarr.Tests.csproj -p:RunAnalyzers=false
```

Requires the .NET 8 SDK. The Lidarr commit this builds against is
pinned in `lidarr-version.txt`; Lidarr's develop branch moves fast, so
bump the pin when updating.

## License

MIT
