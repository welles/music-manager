using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MusicManager.Core.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<InboxGroup> Groups => Set<InboxGroup>();
    public DbSet<TrackFile> Files => Set<TrackFile>();
    public DbSet<FileEvent> Events => Set<FileEvent>();
    public DbSet<ArtistRule> ArtistRules => Set<ArtistRule>();
    public DbSet<SourceProfile> SourceProfiles => Set<SourceProfile>();
    public DbSet<SettingsEntry> Settings => Set<SettingsEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot order by DateTimeOffset; store as ticks (UTC) instead.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<InboxGroup>(e =>
        {
            e.HasIndex(x => x.RelativeFolder);
            e.HasMany(x => x.Files).WithOne(x => x.Group).HasForeignKey(x => x.GroupId);
        });

        b.Entity<TrackFile>(e =>
        {
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.RelativePath);
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Decision).HasConversion<string>();
            e.Json(x => x.OriginalTags);
            e.Json(x => x.CleanedTags);
            e.Json(x => x.FinalTags);
            e.Json(x => x.Candidates);
            e.Json(x => x.AiDecision);
            e.Json(x => x.AiSuggestion);
            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.TrackFileId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FileEvent>(e => e.Property(x => x.Level).HasConversion<string>());
        b.Entity<ArtistRule>(e => e.Property(x => x.Kind).HasConversion<string>());
        b.Entity<SourceProfile>(e => e.Json(x => x.DeleteTags));
    }
}

internal static class JsonPropertyExtensions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    public static void Json<TEntity, TProp>(this EntityTypeBuilder<TEntity> e, System.Linq.Expressions.Expression<Func<TEntity, TProp>> prop)
        where TEntity : class
    {
        e.Property(prop)
            .HasConversion(
                v => JsonSerializer.Serialize(v, Options),
                s => JsonSerializer.Deserialize<TProp>(s, Options)!,
                new ValueComparer<TProp>(
                    (a, c) => JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(c, Options),
                    v => JsonSerializer.Serialize(v, Options).GetHashCode(),
                    v => JsonSerializer.Deserialize<TProp>(JsonSerializer.Serialize(v, Options), Options)!));
    }
}
