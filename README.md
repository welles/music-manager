# Music Manager

Self-hosted replacement for MusicBrainz Picard. New audio files dropped into an inbox
are identified (Chromaprint/AcoustID, MusicBrainz), tagged with your own rules,
converted to M4A/AAC and filed into the Navidrome library. Uncertain cases go to
a review queue in the web frontend, optionally after Claude had a look first.

## Pipeline

1. **Inbox**: the inbox is polled. Every first-level folder is one group (for source
   folders such as `musicfab/` the folder below that). Loose files in the root form
   one group. A group starts once nothing in it has changed for the quiet period
   (default 60 s). The dashboard can also start it right away.
2. **Pre-cleaning**: source profile rules (e.g. MusicFab), removal of video noise,
   artist/title taken from the file name. Optionally Claude cleans up messy tags.
3. **Identification**: existing MusicBrainz tags are used first, so files that were
   already assigned keep their release. Otherwise the fingerprint goes to AcoustID,
   with a MusicBrainz text search as fallback. Every file gets an AcoustID, even
   when it was already assigned.
4. **Release selection**: every release of the matching recordings is scored (old
   tags, release type, group context, date, country, format, length). Close calls
   go to Claude, then to the review queue.
5. **Tagging**: tags are built from the MusicBrainz data with the own rules (see
   [docs/tagging-rules.md](docs/tagging-rules.md)). `ARTISTS`/`ALBUMARTISTS` are
   written as multi-value tags for Navidrome.
6. **Conversion**: AAC streams are only remuxed. Everything else is encoded to AAC
   (256 kbps). `+faststart` is set, and a 500 px cover is embedded.
7. **Filing**: `{album artist}/{album}/{album artist} - {title}.m4a`. Images move
   along, the original goes to the trash, and empty inbox folders are removed.
   Existing target files are never overwritten (possible duplicate → review).

## Running with Docker

The image is built by GitHub Actions and published as `ghcr.io/welles/music-manager`
(tags: `latest`, `main`, `sha-…`, versions from `v*` tags).

```bash
cp .env.example .env   # set paths, user IDs and API keys
docker compose up -d
```

| Volume   | Purpose                                                    |
|----------|------------------------------------------------------------|
| `/inbox` | New files                                                  |
| `/music` | Navidrome library (the same folder Navidrome reads)        |
| `/trash` | Replaced/discarded files, deleted after the retention time |
| `/data`  | SQLite database, work files, Data Protection keys          |

| Environment variable              | Purpose                                                   |
|-----------------------------------|-----------------------------------------------------------|
| `MusicManager__AcoustIdApiKey`    | AcoustID application key (https://acoustid.org/new-application) |
| `MusicManager__AnthropicApiKey`   | Claude API key (optional, enables the AI features)        |
| `MusicManager__MusicBrainzContact`| Contact in the MusicBrainz User-Agent                     |
| `MusicManager__MusicBrainzServer` | Optional own MusicBrainz mirror                           |
| `MusicManager__InboxPath` …       | Override the container paths (default `/inbox`, `/music`, `/trash`, `/data`) |

The container runs as `PUID:PGID` (see `docker-compose.yml`), so filed files belong
to the owner of the music share.

The app has no login of its own. Run it behind the Cloudflare Tunnel and protect it
with Cloudflare Access. Forwarded headers are honored.

## Development

```bash
dotnet test
dotnet run --project src/MusicManager.Web --launch-profile http
```

`appsettings.Development.json` uses `dev-data/` in the repository. `ffmpeg`, `ffprobe`
and `fpcalc` must be on the `PATH`; on NixOS run
`nix-shell -p ffmpeg chromaprint --run "dotnet run --project src/MusicManager.Web"`.

Database changes: `dotnet ef migrations add <Name> --project src/MusicManager.Core --output-dir Data/Migrations`
(the tool is in the local tool manifest, `dotnet tool restore`).

## Not implemented yet

- Duplicate view with A/B player and library index
- Fallback sources (Spotify, Deezer, Discogs)
- Periodic re-check of manually tagged files, MusicBrainz release editor seeding
- ReplayGain calculation
