# Music Manager

Self-hosted replacement for MusicBrainz Picard. New audio files dropped into an inbox
are identified (Chromaprint/AcoustID, MusicBrainz), tagged with your own rules,
converted to M4A/AAC and filed into the music library. Uncertain cases go to
a review queue in the web frontend, optionally after Claude had a look first. The
library is also served through a minimal OpenSubsonic API, so a player such as
Symfonium can use it instead of Navidrome.

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
   tags incl. their date, exact album name, release type, group context, date, country, format, length). Close calls
   go to Claude, then to the review queue.
5. **Tagging**: tags are built from the MusicBrainz data with the own rules (see
   [docs/tagging-rules.md](docs/tagging-rules.md)). `ARTIST` and `ALBUMARTIST` hold the
   individual artists as multi-value tags (no joined "A feat. B" string).
6. **Conversion**: AAC streams are only remuxed. Everything else is encoded to AAC
   (256 kbps). `+faststart` is set, and a 500 px cover is embedded.
7. **Filing**: `{album artist}/{album}/{album artist} - {title}.m4a`. Images move
   along, the original goes to the trash, and empty inbox folders are removed.
   Before filing, every file is checked against the library index (MusicBrainz ID,
   fingerprint, artist/title + duration); duplicates wait in the duplicate view with
   an A/B player (keep new / keep existing / keep both). The library itself can be
   checked for existing duplicates as well.

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
| `/music` | Music library (the same folder Navidrome reads, if used)   |
| `/trash` | Replaced/discarded files, deleted after the retention time |
| `/data`  | SQLite database, work files, Data Protection keys, cover cache |

| Environment variable              | Purpose                                                   |
|-----------------------------------|-----------------------------------------------------------|
| `MusicManager__AcoustIdApiKey`    | AcoustID application key (https://acoustid.org/new-application) |
| `MusicManager__AnthropicApiKey`   | Claude API key (optional, enables the AI features)        |
| `MusicManager__MusicBrainzContact`| Contact in the MusicBrainz User-Agent                     |
| `MusicManager__SpotifyClientId`, `MusicManager__SpotifyClientSecret` | Spotify app for the fallback search (optional; Deezer needs no key) |
| `MusicManager__MusicBrainzServer` | Optional own MusicBrainz mirror                           |
| `MusicManager__SubsonicApiKey`    | API key for the Subsonic API (optional; without it the API is off) |
| `MusicManager__LastFmApiKey`      | Last.fm API key (optional, https://www.last.fm/api/account/create) – genre fallback |
| `MusicManager__InboxPath` …       | Override the container paths (default `/inbox`, `/music`, `/trash`, `/data`) |

The container runs as `PUID:PGID` (see `docker-compose.yml`), so filed files belong
to the owner of the music share.

The app has no login of its own. Run it behind the Cloudflare Tunnel and protect it
with Cloudflare Access. Forwarded headers are honored.

### Data in the files

The library files are the source of truth for everything that describes a song. Besides the tags, files
written by this tool carry their song ID (`MUSICMANAGER_SONG_ID`, the Subsonic ID Symfonium keys favorites
and playlists on), the date they were added (`MUSICMANAGER_ADDED`), the favorite (`MUSICMANAGER_FAVORITE`)
and the rating (`RATING`, 1–5), plus the origin of the tags and genres. The database mirrors them into the
files a few seconds after a change. If the database is lost, a scan restores song IDs, added dates,
favorites and ratings from the files; a file moved or renamed outside the tool keeps its ID. Play counts,
the inbox history and settings stay in the database (a play would otherwise rewrite the file).

Tags edited with another tool are noticed: when the app writes a file, it stores a hash of the tags it
wrote (`MUSICMANAGER_TAGS_HASH`, without the song markers above) and one of the cover
(`MUSICMANAGER_COVER_HASH`). A scan that finds different tags marks the file as "extern geändert" in the
library. Re-tagging such a file keeps the edits (the rules are applied to its current tags, and it counts
as manually tagged from then on) unless "Externe Änderungen verwerfen" rebuilds it from MusicBrainz. Older
files get their hash with the next marker sync.

Playlists are kept as M3U8 files in the library's `Playlists` folder, with paths relative to the file so
other players can read them. Extra `#MUSICMANAGER-` comment lines keep the playlist ID, comment and the song
ID of every entry, so a song that is temporarily missing is not lost. The sync works both ways: changes in
the app (Symfonium) and moved songs rewrite the file within seconds; a file edited, added or renamed in the
folder is taken over within a minute (the newer change wins). A lost database gets all playlists back, with
their old IDs. A deleted file is written again; delete playlists in the app.

### App and notifications

The web app can be installed as a PWA (HTTPS required, e.g. through the tunnel). Under
Settings → Notifications each device can subscribe to Web Push: after a batch, it gets one
summary when new files wait for review, have no MusicBrainz match, look like duplicates or
failed. The VAPID key pair is created on first start as `vapid.pem` in the data folder; keep it,
otherwise all devices have to subscribe again.

### Subsonic API (Symfonium)

Under `/rest` the library is served as a minimal [OpenSubsonic](https://opensubsonic.netlify.app)
API, enough for [Symfonium](https://symfonium.app/) to use Music Manager as its media source
instead of Navidrome: browsing by tags, search (Symfonium's full sync), streaming with seeking,
covers, favourites, ratings, play counts and playlists. Songs keep their ID when a file is
retagged, moved or replaced (duplicate "keep new"), so favourites, play counts and playlist
entries stay with the song. Album and artist IDs come from the MusicBrainz IDs (otherwise from
the names), so stars and ratings of albums and artists are lost when those tags change. User data
of a song that is missing from the library is kept and comes back with the file.

Set `MusicManager__SubsonicApiKey` to a long random value (e.g. `openssl rand -hex 32`). Without
it every call fails. The API stays behind Cloudflare Access; Symfonium gets through with a
service token:

1. Zero Trust → Access → Service Auth: create a service token.
2. Add a policy with the action *Service Auth* for that token to the Music Manager Access
   application (the browser login stays as it is).
3. In Symfonium add an OpenSubsonic media provider: server URL `https://<your host>` (without
   `/rest`), enable "API-Schlüssel benutzen" and enter the key.
4. Under "Custom-Header schicken" add `CF-Access-Client-Id: <client id>` and
   `CF-Access-Client-Secret: <client secret>` of the service token.

Limits:

- JSON only (no XML), API key authentication only (no username/password or token auth).
- No transcoding: `stream` always serves the original file (`maxBitRate` and `format` are ignored).
- No folder browsing (`getIndexes`, `getMusicDirectory`); browsing by tags only.
- One user (the owner); there is no user management.
- Casting (Chromecast/UPnP) does not work through Cloudflare Access: the cast device fetches the
  stream URL itself and does not send the custom headers.

Resized covers are cached under `<data>/cache/covers`; the folder can be deleted at any time.

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

- Discogs as an additional fallback source
- Periodic re-check of manually tagged files, MusicBrainz release editor seeding
- ReplayGain calculation
