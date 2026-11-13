# Tagging rules

The rules replace the Picard scripts (`Automatic Script`, `File Renaming Script`,
`MusicFab Import`). They were not ported command by command. Each script command was
taken for what it was meant to achieve, and that goal is implemented with the data
MusicBrainz provides. The Picard scripting language often forced workarounds that are no
longer needed here.

| Picard script | Purpose | Implementation |
|---|---|---|
| `$delete(artistsort/albumartistsort/composersort)` | No sort tags in the files | Files are written from scratch (`ffmpeg -map_metadata -1`), sort tags are never written. |
| `$setmulti(artist,%artists%)` + `$delete(artists)` | Every artist as its own value in `ARTIST`, so players link the song to every artist | `ARTIST` is written as a multi-value field with the individual artists from the structured MusicBrainz credits (`TagBuilder`); no joined "A feat. B" string and no separate `ARTISTS` tag. |
| `_artist_splitter_strings` + `$rreplace` | Split composed artist strings | Only needed for data **without** credits (manual input, fallback sources): `ArtistRuleSet.Split`. |
| Comparing `artist` / `albumartist` | Album artist as a multi-value list as well | `ALBUMARTIST` is a multi-value field from the release's artist credits. Without credits, the album artist string is split, and if it names the same people as the artist, the artist list is reused. |
| `$rsearch(%title%, (… Remix))` | Remixer is credited as an artist | First choice is the recording's MusicBrainz **remixer relationship**; the title pattern `(Name Remix)` is only a fallback. The remixer is added to `ARTIST`. |
| Remove ` (explicit)` / ` (album version)` | Variant markers do not belong in the title | Case-insensitive, round and square brackets, also "Explicit Version". |
| `_force_split_artists` | MusicBrainz artists like "Audio88 & Yassin" should show up under their individual members | List editable in the frontend; applies to individual artists in the artist lists. |
| `_force_replace_artists` | Unify artist names ("Bob Seger & the Silver Bullet Band" → "Bob Seger") | Applied **per credit** (exact name), before the display string is assembled. No substring replacement in composed strings. |
| "Bruce Hornsby & the Range" → "… and the Range" | (workaround) keeps the band name from being split at " & " | Replaced by the **exception list** (`NoSplit`): such names are never split. |
| `$noop(MEMENTO IDEMPOTENCY)` | Running the script again must not change anything | `TaggingRules.Apply` is a pure function and is tested for idempotence. |
| File Renaming Script | `{albumartist}/{album}/{artist} - {title}` (final, lost version of the script) | `PathBuilder`: folder uses the first album artist, the file name the first artist of the track (as in the existing library, e.g. `Various Artists/Shell Shock/Fox Stevenson - Sandblast`); fallback to the first artist and then the artist string. Names are kept exactly as tagged (Unicode, NFC – decision 2026-11-11: the Picard backup had `ascii_filenames` on, but the library is mostly Unicode). Only `/` → `∕` (U+2215) and a leading `.` → `․` (U+2024, hidden folders are skipped by the library index); long names are cut to 240 bytes. |
| MusicFab Import | Source-specific pre-cleaning | Source profiles (inbox subfolder → rules), editable in the frontend. MusicFab also drops album tags that are playlist names ("… Playlist", the playlist in `show`). Encoder tags never survive: the writer clears all tags first. |

Additionally (new):

- No relationship tags (COMPOSER, LYRICIST, WRITER, PRODUCER, MIXER, ENGINEER, ARRANGER, WORK)
  and in particular no PERFORMER/instrument credits: the writer clears all tags first and only
  writes its own field list, so such tags from Picard-tagged files disappear on import or re-tag.
- Every written file carries `MUSICMANAGER_RULES` (rules version) to find files tagged by older rules.

- Features in the title (`Song (feat. B)`) are moved into the artist field.
- Typographic punctuation is converted to plain ASCII punctuation (Picard `convert_punctuation`).

## Genres

Picard's genre options (`use_genres`, `artists_genres`, `max_genres=10`, `min_genre_usage=50`, the
`genres_filter`, no folksonomy) are the defaults. Picard only knew MusicBrainz; the app goes further
because a quarter of the first files got no genre at all (`GenreService`, decision 2026-11-11). The
first source with a result wins:

1. MusicBrainz genres of the **recording** (double weight, more specific e.g. for remixes) plus the
   release and release group.
2. **Last.fm** top tags of the track, then of the album – only names that MusicBrainz lists as genres,
   so tags like "seen live" or "2010s" never get in. Needs `MusicManager__LastFmApiKey`.
3. MusicBrainz genres of the credited **artists**. In Picard they counted as much as the album's, which
   gave whole discographies the same broad genres; now they are only a fallback.
4. The genres **already in the file**, so re-tagging or importing the old library never loses them.
5. Last.fm tags of the **artist**, looked up by MusicBrainz ID only – names are ambiguous ("August Moon"
   finds a Finnish death metal band).

From the remaining votes, genres with at least 50 % of the strongest one are kept, at most 10. They
are written in title case as in the existing library ("Drum And Bass", "Hip Hop"), abbreviations in
capitals ("UK Garage", "EDM", "MPB", "R&B"), and Last.fm/Deezer spellings ("dnb", "hip-hop") are
mapped to the MusicBrainz name. Own spellings can be set in the settings ("Drum And Bass = Drum &
Bass"; an empty right side drops a genre). Genres given by Deezer or entered by hand only get the
spelling rules.
