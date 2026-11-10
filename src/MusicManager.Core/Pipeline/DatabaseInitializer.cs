using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Configuration;
using MusicManager.Core.Data;

namespace MusicManager.Core.Pipeline;

/// <summary>Applies migrations and seeds the rules taken from the previous Picard scripts.</summary>
public sealed class DatabaseInitializer(IDbContextFactory<AppDbContext> dbFactory, SettingsService settings, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        if (!await db.Settings.AnyAsync(ct))
        {
            logger.LogInformation("Seeding default settings and rules from the Picard scripts");
            await settings.SaveAsync(new AppSettings(), ct);

            db.ArtistRules.AddRange(
                new ArtistRule { Kind = ArtistRuleKind.ForceSplit, Pattern = "Audio88 & Yassin", Comment = "aus Picard-Skript" },
                new ArtistRule { Kind = ArtistRuleKind.ForceSplit, Pattern = "Ben Caplan & The Casual Smokers", Comment = "aus Picard-Skript" },
                new ArtistRule
                {
                    Kind = ArtistRuleKind.ForceReplace, Pattern = "Bob Seger & the Silver Bullet Band", Replacement = "Bob Seger",
                    Comment = "aus Picard-Skript",
                },
                new ArtistRule
                {
                    Kind = ArtistRuleKind.NoSplit, Pattern = "Bruce Hornsby & the Range",
                    Comment = "ersetzt die Picard-Umbenennung in \"Bruce Hornsby and the Range\"",
                });

            db.SourceProfiles.Add(new SourceProfile
            {
                Name = "MusicFab",
                FolderName = "musicfab",
                SplitCommaWithoutSpace = true,
                DeleteTags = ["show", "discnumber"],
                TrimTrackTotal = true,
            });

            await db.SaveChangesAsync(ct);
        }

        // Files that were being processed when the app stopped are processed again.
        await db.Files.Where(f => f.Status == FileStatus.Processing)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, FileStatus.Queued), ct);
    }
}
