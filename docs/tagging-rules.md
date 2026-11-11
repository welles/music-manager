# Tagging rules

The rules replace the Picard scripts (`Automatic Script`, `File Renaming Script`,
`MusicFab Import`). They were not ported command by command. Each script command was
taken for what it was meant to achieve, and that goal is implemented with the data
MusicBrainz provides. The Picard scripting language often forced workarounds that are no
longer needed here.

| Picard script | Purpose | Implementation |
|---|---|---|
| `$delete(artistsort/albumartistsort/composersort)` | No sort tags in the files | Files are written from scratch (`ffmpeg -map_metadata -1`), sort tags are never written. |
| `$setmulti(artist,%artists%)` + `$delete(artists)` | Every artist as its own value in `ARTIST`, so Navidrome links the song to every artist | `ARTIST` is written as a multi-value field with the individual artists from the structured MusicBrainz credits (`TagBuilder`); no joined "A feat. B" string and no separate `ARTISTS` tag. |
| `_artist_splitter_strings` + `$rreplace` | Split composed artist strings | Only needed for data **without** credits (manual input, fallback sources): `ArtistRuleSet.Split`. |
| Comparing `artist` / `albumartist` | Album artist as a multi-value list as well | `ALBUMARTIST` is a multi-value field from the release's artist credits. Without credits, the album artist string is split, and if it names the same people as the artist, the artist list is reused. |
| `$rsearch(%title%, (… Remix))` | Remixer is credited as an artist | First choice is the recording's MusicBrainz **remixer relationship**; the title pattern `(Name Remix)` is only a fallback. The remixer is added to `ARTIST`. |
| Remove ` (explicit)` / ` (album version)` | Variant markers do not belong in the title | Case-insensitive, round and square brackets, also "Explicit Version". |
| `_force_split_artists` | MusicBrainz artists like "Audio88 & Yassin" should show up under their individual members | List editable in the frontend; applies to individual artists in the artist lists. |
| `_force_replace_artists` | Unify artist names ("Bob Seger & the Silver Bullet Band" → "Bob Seger") | Applied **per credit** (exact name), before the display string is assembled. No substring replacement in composed strings. |
| "Bruce Hornsby & the Range" → "… and the Range" | (workaround) keeps the band name from being split at " & " | Replaced by the **exception list** (`NoSplit`): such names are never split. |
| `$noop(MEMENTO IDEMPOTENCY)` | Running the script again must not change anything | `TaggingRules.Apply` is a pure function and is tested for idempotence. |
| File Renaming Script | `{albumartist}/{album}/{artist} - {title}` (final, lost version of the script) | `PathBuilder`: folder uses the first album artist, the file name the first artist of the track (as in the existing library, e.g. `Various Artists/Shell Shock/Fox Stevenson - Sandblast`); fallback to the first artist and then the artist string, ASCII only, Windows-compatible, `/` → `_`. |
| MusicFab Import | Source-specific pre-cleaning | Source profiles (inbox subfolder → rules), editable in the frontend. |

Additionally (new):

- No relationship tags (COMPOSER, LYRICIST, WRITER, PRODUCER, MIXER, ENGINEER, ARRANGER, WORK)
  and in particular no PERFORMER/instrument credits: the writer clears all tags first and only
  writes its own field list, so such tags from Picard-tagged files disappear on import or re-tag.
- Every written file carries `MUSICMANAGER_RULES` (rules version) to find files tagged by older rules.

- Features in the title (`Song (feat. B)`) are moved into the artist field.
- Typographic punctuation is converted to plain ASCII punctuation (Picard `convert_punctuation`).
