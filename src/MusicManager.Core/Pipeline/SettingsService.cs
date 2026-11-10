using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Configuration;
using MusicManager.Core.Data;
using MusicManager.Core.Rules;

namespace MusicManager.Core.Pipeline;

/// <summary>Runtime settings, artist rules and source profiles stored in the database.</summary>
public sealed class SettingsService(IDbContextFactory<AppDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private AppSettings? _settings;

    public event Action? Changed;

    public async Task<AppSettings> GetAsync(CancellationToken ct = default)
    {
        if (_settings != null) return _settings;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Settings.OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        _settings = row == null ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(row.Json, JsonOptions) ?? new AppSettings();
        return _settings;
    }

    /// <summary>Returns a detached copy for editing.</summary>
    public async Task<AppSettings> GetCopyAsync(CancellationToken ct = default) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(await GetAsync(ct), JsonOptions), JsonOptions)!;

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Settings.OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        if (row == null)
            db.Settings.Add(new SettingsEntry { Json = json });
        else
            row.Json = json;
        await db.SaveChangesAsync(ct);
        _settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        Changed?.Invoke();
    }

    public async Task<ArtistRuleSet> GetArtistRulesAsync(CancellationToken ct = default)
    {
        var settings = await GetAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rules = await db.ArtistRules.AsNoTracking().ToListAsync(ct);
        return ArtistRuleSet.FromRules(rules, settings.ArtistSplitters);
    }

    public async Task<List<SourceProfile>> GetSourceProfilesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SourceProfiles.AsNoTracking().Where(p => p.Enabled).ToListAsync(ct);
    }
}
