# Music Manager

Self-hosted replacement for MusicBrainz Picard. New audio files dropped into an inbox
are identified (Chromaprint/AcoustID, MusicBrainz), tagged with your own rules,
converted to M4A/AAC and filed into the music library. Uncertain cases go to
a review queue in the web frontend, optionally after Claude had a look first. The
library is served through its own OpenSubsonic API, so a player such as Symfonium
streams straight from Music Manager.

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
   Files MusicBrainz does not know get suggestions from Spotify and Deezer (links in
   the tags, else a search). A **YouTube link** in the tags (e.g. `PURL`/`COMMENT` from
   yt-dlp, issue #31) is read via the YouTube Data API: Claude works out artist, title
   and album from the video title, channel and description, the upload date becomes the
   date, and the thumbnail, cut square, the cover. Without an album it is filed as a
   single. The suggestion is taken over in the review like the Spotify/Deezer ones.
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
   Each duplicate's audio is also compared with the library file over the whole
   track (Chromaprint, 10-second sections): if it is the same audio, only encoded
   differently, it is marked "Gleiche Aufnahme", and all such new files can be
   discarded in one go. A version with a voice or other sound laid over the music
   (a commentary track) stays for review. Open duplicates from before this check
   are compared once after the start.

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
| `/music` | Music library                                              |
| `/trash` | Replaced/discarded files, deleted after the retention time; the **Papierkorb** page lists them by day with their sizes and empties the trash or single days |
| `/data`  | SQLite database, work files, Data Protection keys, cover and transcode cache |

| Environment variable              | Purpose                                                   |
|-----------------------------------|-----------------------------------------------------------|
| `MusicManager__AcoustIdApiKey`    | AcoustID application key (https://acoustid.org/new-application) |
| `MusicManager__AnthropicApiKey`   | Claude API key (optional, enables the AI features)        |
| `MusicManager__MusicBrainzContact`| Contact in the MusicBrainz User-Agent                     |
| `MusicManager__SpotifyClientId`, `MusicManager__SpotifyClientSecret` | Spotify app for the fallback search (optional; Deezer needs no key) |
| `MusicManager__YouTubeApiKey`     | YouTube Data API v3 key (optional): YouTube links in the tags become a tag suggestion (needs the AI) |
| `MusicManager__MusicBrainzServer` | Optional own MusicBrainz mirror                           |
| `MusicManager__SubsonicApiKey`    | API key for the Subsonic API (optional; without it the API is off) |
| `MusicManager__LastFmApiKey`      | Last.fm API key (optional, https://www.last.fm/api/account/create) – genre fallback |
| `MusicManager__MusicFabUrl`, `MusicManager__MusicFabApiKey` | MusicFab.Manager (optional, see [MusicFab](#musicfab)) |
| `MusicManager__TranscodeCacheMb`  | Size limit of the transcode cache in MB (default 2048, 0 = off) |
| `MusicManager__InboxPath` …       | Override the container paths (default `/inbox`, `/music`, `/trash`, `/data`) |

The container runs as `PUID:PGID` (see `docker-compose.yml`), so filed files belong
to the owner of the music share.

The app has no login of its own. Run it behind the Cloudflare Tunnel and protect it
with Cloudflare Access. Forwarded headers are honored.

### MusicFab

With `MusicManager__MusicFabUrl` (e.g. `http://musicfab:5810`) and `MusicManager__MusicFabApiKey`
(its `FAB_API_KEY`), tracks, albums and playlists can be requested from **MusicFab.Manager**, the
manager in the MusicFab container of [fab-containers](https://github.com/welles/fab-containers).
It opens the URL in MusicFab, selects all tracks and downloads them into this inbox's `musicfab/`
folder (one folder per album or playlist), where they go through the pipeline like any other file.

- The **MusicFab** page takes a URL (e.g. `https://music.youtube.com/playlist?list=…`) and lists the
  requests with their state, progress, errors and the inbox folder they arrive in. While one runs,
  its state is fetched every 10 seconds.
- A Spotify playlist page offers the download for each **missing** track
  (`https://open.spotify.com/track/…`); that needs MusicFab logged in to Spotify.
- The MusicFab container must mount this inbox as its `/output`, and the Music Manager must reach its
  port 5810.
- Behind Cloudflare Access (e.g. `https://musicfab.example.com`), a service token whose policy lets it
  through goes into `MusicManager__MusicFabAccessClientId` and `MusicManager__MusicFabAccessClientSecret`;
  it is sent as `CF-Access-Client-Id`/`CF-Access-Client-Secret`.

### Data in the files

The library files are the source of truth for everything that describes a song. Besides the tags, files
written by this tool carry their song ID (`MUSICMANAGER_SONG_ID`, the Subsonic ID Symfonium keys favorites
and playlists on), the date they were added (`MUSICMANAGER_ADDED`), the favorite (`MUSICMANAGER_FAVORITE`)
and the rating (`RATING`, 1–5), plus the origin of the tags and genres. The database mirrors them into the
files a few seconds after a change. If the database is lost, a scan restores song IDs, added dates,
favorites and ratings from the files; a file moved or renamed outside the tool keeps its ID. Play counts,
the play history, the inbox history and settings stay in the database (a play would otherwise rewrite the file).

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

The loudness of every filed song is measured (EBU R128) and written as ReplayGain tags (`replaygain_track_gain`,
`replaygain_track_peak`, reference −18 LUFS); the audio itself stays untouched. Players that honor ReplayGain, such
as Symfonium, level the volume with them, the Subsonic API delivers the values as `replayGain`, and the player in
the web app applies them too. A song that has the tags counts as normalized. Settings → Metadaten → "Lautstärke
der Bibliothek messen" measures the library songs that have none yet.

Album covers are embedded into the files at 500 px (setting "Cover-Größe in der Datei") and also kept in
full size, up to 1200 px, as `cover.jpg` in the album folder. The Subsonic API serves that file instead of
the embedded cover. Any `cover`, `folder`, `front` or
`albumart` image (JPEG or PNG) in the folder counts, and an existing one is never overwritten, so an own
image can simply be put there. This only applies to a folder that holds exactly one album. For albums that
were filed earlier, Settings → Metadaten → "Fehlende Album-Cover nachladen" loads the covers from the Cover
Art Archive; for albums without a MusicBrainz release or without a cover there, Deezer (1000 px) and Spotify
(640 px) are searched. Albums found nowhere keep their embedded cover.

### Reporting songs from the car

Symfonium in Android Auto has no button to flag a song, so a quick favorite does it: star a song and unstar it
again within 10 seconds, and it is reported for a later check (e.g. an unwanted version or a glitch in the audio).
The favorite itself is gone again. The **Meldungen** page lists the reports with the song, a note field and links
to edit or re-tag it; "Erledigt" closes a report, and reporting the same song again while it is open counts up.
Only a fresh star counts: unstarring an older favorite is no report. The window is set under Settings → Metadaten
→ "Melden per Favorit (s)" (0 = off). Reports are kept in the database only.

### Statistics and history

The **Statistik** page (issue #20) shows, for the last 7 or 30 days, one year or everything: plays, skipped
songs and the skip rate, listening time, new favorites and reports. It also has a chart per day, week or month, the
most played and most skipped songs, the artists (each artist of "A feat. B" counts), and the history itself, grouped
by day and filterable by kind. The data comes from the Subsonic API: `scrobble` with `submission=false` ("now
playing") is a start, with `submission=true` a play, and `star`/`unstar` (a quick unstar as a report).

A **skip** is a start that got no play: no play of that song from shortly before the start until its length plus
15 minutes after it. It only counts once the same player started another song or that time is over; until then it
shows as "Läuft". Skips are worked out each time the page loads, so a play that arrives late (an offline player
sends its scrobbles later) still turns the skip into a play. A repeated "now playing" of the song that is running
(e.g. after a pause) is not a new start. Without "now playing" from the player, no skip can be seen; the page says
so. The history starts with this version; earlier plays only count in the play counts of the songs. It is kept in
the database only.

### App and notifications

The web app can be installed as a PWA (HTTPS required, e.g. through the tunnel). Under
Settings → Notifications each device can subscribe to Web Push: after a batch, it gets one
summary when new files wait for review, have no MusicBrainz match, look like duplicates or
failed. The VAPID key pair is created on first start as `vapid.pem` in the data folder; keep it,
otherwise all devices have to subscribe again.

### Subsonic API (Symfonium)

Under `/rest` the library is served as a minimal [OpenSubsonic](https://opensubsonic.netlify.app)
API, enough for [Symfonium](https://symfonium.app/) to use Music Manager as its media source:
browsing by tags, search (Symfonium's full sync), streaming with seeking,
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

Transcoding: `stream` serves the original file unless the client asks for less. With `maxBitRate`
below the file's bitrate or another `format` (`mp3`, `opus`, `aac`; `raw` = always the original)
the song is encoded on the fly with ffmpeg and starts right away, which makes skipping much faster
over a slow uplink. Without a format it becomes MP3; the bitrate never goes above the file's own.
Seeking in a transcoded stream works through `timeOffset` (OpenSubsonic `transcodeOffset`).
`download` always serves the original. In Symfonium set it per network in the media provider's
transcoding settings, e.g. Opus 128 kbit/s on mobile data.

Finished transcodes are kept under `<data>/cache/transcodes` (limit `MusicManager__TranscodeCacheMb`,
default 2 GB, about 500 songs at 128 kbit/s; what was used longest ago goes first). The first
request of a song in a format is encoded while it is sent and stored at the same time; when the
player hangs up early (skip), the encode still runs to the end, so the song is complete in the
cache next time (only complete, error-free transcodes are kept); later ones
get the file right away, with its length and with ranges, so players know the duration at once
(e.g. Symfonium's waveform). When a song's original changes, is replaced, moved or deleted, its
cached transcodes are removed (the library index notices it) and it is encoded again on the next
request; jumps into a song (`timeOffset`) are not cached. The folder can be deleted at any time.

Every stream and download writes one log line (category `MusicManager.Web.Subsonic.Stream`):
song, what was sent (original, range, transcoded format and bitrate), size, time, rate, when the
first byte went out and whether the client skipped. The time is taken where the bytes leave for
the Cloudflare tunnel, so for songs of several MB it shows how fast the way to the phone is, e.g.
`docker logs music_manager 2>&1 | grep "Stream "`. URLs are not logged, they carry the API key.

Playlists kept by Music Manager come first in every client and are read-only there (OpenSubsonic
`readonly`); they are worked out on every request:

- **Alle** – every song, in a new random order each time the playlist is loaded (`validUntil` tells
  clients the content is only valid right now). A player keeps the order once playback started.
- **Noch nie gespielt** – songs without a single play reported by a client (`scrobble` with
  `submission=true`); songs that were only started and skipped stay in. Shuffled like "Alle".
- **Favoriten** – starred songs, the most recently starred first.

**Spotify playlists** (issues #19, #21): on the *Playlists* page you connect your Spotify account
once and add playlists; Music Manager reads their tracks every 6 hours (and on "Jetzt abgleichen"),
only again when Spotify's snapshot changed. Clients get them read-only with the Spotify name, owner
"Spotify", a comment like "Spotify-Playlist · von … · 12 von 40 Titeln vorhanden" and the Spotify
picture with a green badge; they contain only the tracks that are in the library (matched by ISRC,
else by title, artist and length), so new songs show up without another sync. The page shows all
tracks, the missing ones greyed, with a filter for them.

Since February 2026 Spotify gives out the tracks only with a user login and only for playlists you
own or collaborate on (the app owner needs Premium). Playlists of others you follow can be mirrored
into an own playlist with [Soundiiz](https://soundiiz.com) (Premium: 20 automatic daily syncs,
method "replace tracks"); add the copy. Setup:

1. In the [Spotify dashboard](https://developer.spotify.com/dashboard) add
   `https://<your host>/spotify/callback` as redirect URI of the app whose ID and secret are in
   `MusicManager__SpotifyClientId` / `MusicManager__SpotifyClientSecret`
   (`MusicManager__SpotifyRedirectUri` overrides the address worked out from the request).
2. *Playlists* → "Mit Spotify verbinden", then "Hinzufügen". Your **liked songs**
   ("Lieblingssongs") are offered first there: Spotify keeps them as saved tracks, not as a
   playlist, so they are read on every sync (latest liked first) and get their own cover.

Optionally Music Manager keeps a private Spotify playlist **"Fehlt im Music Manager"** with all
tracks of the kept playlists that are not in the library (each once): switch on the *Playlists*
page. It is updated with every sync – tracks that arrived in the library leave it, new missing ones
join – and created again if it was deleted on Spotify. This needs the permission to write private
playlists (`playlist-modify-private`), the liked songs `user-library-read`; a login from before they
were asked for has to connect again (the chosen playlists and settings stay).

Each has its own cover (`getCoverArt` with the playlist's ID; pictures in
`src/MusicManager.Web/wwwroot/playlist-covers`, SVG sources in `docs/playlist-covers`). Each load of
one of them writes a log line (`Smart playlist … loaded by …`). Playlists created in a client stay
editable as before.

Limits:

- JSON only (no XML), API key authentication only (no username/password or token auth).
- No folder browsing (`getIndexes`, `getMusicDirectory`); browsing by tags only.
- One user (the owner); there is no user management.
- Casting (Chromecast/UPnP) does not work through Cloudflare Access: the cast device fetches the
  stream URL itself and does not send the custom headers.

Resized covers are cached under `<data>/cache/covers`; the folder can be deleted at any time.
Players keep covers in a cache of their own: after covers were loaded for existing albums, clear the image
cache in Symfonium to see them.

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
