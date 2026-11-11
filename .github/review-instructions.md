# Worauf das KI-Review achtet

Anweisungen für das Claude-Review (`.github/workflows/claude-review.yml`). Workflow, Prompt und Skripte liegen im zentralen Reviewer [welles/claude-reviewer](https://github.com/welles/claude-reviewer); was nur für dieses Repo gilt, steht hier.

## Projekt

Music Manager ist ein selbst gehosteter Ersatz für MusicBrainz Picard in .NET 10 (ASP.NET Core, EF Core mit SQLite), als Docker-Image betrieben. Neue Dateien in der Inbox werden erkannt (Chromaprint/AcoustID, MusicBrainz), nach eigenen Regeln getaggt (`docs/tagging-rules.md`), nach M4A/AAC konvertiert und in die Musikbibliothek einsortiert. Unsichere Fälle gehen in eine Review-Warteschlange im Web-Frontend, optional nach einer Prüfung durch Claude. Die Bibliothek wird zusätzlich als OpenSubsonic-API unter `/rest` ausgeliefert (für Symfonium). `src/MusicManager.Core` enthält Pipeline, Katalog, Daten und KI, `src/MusicManager.Web` das Frontend und die API, `tests/` die Tests. Ablauf und Betrieb stehen im `README.md`.

## Besonders prüfen

- Die Musikdateien sind die Quelle der Wahrheit: Kein Pfad darf eine Datei der Bibliothek verlieren, überschreiben oder beschädigen. Ersetzte oder verworfene Dateien gehen in den Papierkorb, nicht direkt ins Löschen; Schreiben in Dateien (Tags, `MUSICMANAGER_*`-Felder, Rating) darf bei einem Abbruch keine halbe Datei hinterlassen.
- Song-IDs, Favoriten, Ratings, Play Counts und Playlist-Einträge bleiben am Song, wenn eine Datei neu getaggt, verschoben oder ersetzt wird; nach Verlust der Datenbank muss ein Scan sie aus den Dateien wiederherstellen können.
- Playlists (M3U8 im Ordner `Playlists`) synchronisieren in beide Richtungen: kein Verlust von Einträgen, keine Endlosschleife zwischen Datei und Datenbank, die neuere Änderung gewinnt.
- Pfade: Dateinamen aus Tags oder Anfragen dürfen nicht aus Inbox, Bibliothek, Papierkorb oder Datenordner herausführen (`..`, absolute Pfade, ungültige Zeichen).
- Subsonic-API: jeder Aufruf nur mit dem API-Schlüssel; ohne konfigurierten Schlüssel ist die API aus. Die Web-App hat keinen eigenen Login und verlässt sich auf Cloudflare Access; neue Endpunkte dürfen das nicht umgehen oder Daten ohne Schutz preisgeben.
- Geheimnisse (API-Schlüssel von AcoustID, Anthropic, Spotify, Last.fm, Subsonic, der VAPID-Schlüssel) nicht im Code, in Logs oder in Antworten an den Client.
- Nebenläufigkeit: Inbox-Polling, Pipeline, Sync in die Dateien und Anfragen der API laufen parallel; geteilte Zustände und die Datenbank müssen dafür sicher sein.
- Externe Dienste (MusicBrainz, AcoustID, Spotify, Deezer, Last.fm, Claude): Rate Limits, Timeouts und Fehler dürfen eine Gruppe nicht falsch einsortieren; im Zweifel in die Review-Warteschlange.
- Datenbank: jede Änderung an den Entities braucht eine Migration; Migrationen dürfen bestehende Daten nicht verwerfen.
- Ein Fehler, durch den Dateien verloren gehen, die Bibliothek beschädigt wird oder der Container nicht mehr startet, ist mindestens `fehler`, Datenverlust in der Bibliothek `kritisch`.

Generierte Dateien überspringst du: `Data/Migrations/*.Designer.cs` und `AppDbContextModelSnapshot.cs` (die Migration selbst prüfst du).

## Hinweise

`hinweis` sind z. B. fehlende Tests für eine geänderte Regel der Pipeline, Abweichungen von `docs/tagging-rules.md` ohne Anpassung der Doku und kleine Risiken.

## Bereiche

Sicherheit, Funktion, Daten, Pipeline, Tagging, Subsonic-API, Frontend, Betrieb
