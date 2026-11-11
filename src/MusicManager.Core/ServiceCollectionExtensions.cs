using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MusicManager.Core.Ai;
using MusicManager.Core.Configuration;
using MusicManager.Core.Data;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;

namespace MusicManager.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMusicManager(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MusicManagerOptions>(configuration.GetSection(MusicManagerOptions.Section));
        var options = configuration.GetSection(MusicManagerOptions.Section).Get<MusicManagerOptions>() ?? new MusicManagerOptions();
        Directory.CreateDirectory(options.DataPath);

        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={options.DatabasePath}"));

        const string userAgent = "MusicManager/1.0 (+https://github.com/welles/music-manager)";
        services.AddHttpClient<AcoustIdClient>(c => c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent));
        services.AddHttpClient<CoverArtService>(c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            c.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<SpotifyClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddHttpClient<DeezerClient>(c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            c.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddSingleton<ExternalSourceService>();
        services.AddSingleton<MusicBrainzService>();
        services.AddSingleton<AudioTools>();
        services.AddSingleton<AiService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<Storage>();
        services.AddSingleton<WorkQueue>();
        services.AddSingleton<PipelineNotifier>();
        services.AddSingleton<DuplicateDetector>();
        services.AddSingleton<LibraryDuplicateScanner>();
        services.AddSingleton<LibraryCatalog>();
        services.AddSingleton<RetagService>();
        services.AddHttpClient("wikimedia", c =>
        {
            // Wikimedia requires a descriptive User-Agent.
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            c.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddSingleton<ArtistImageService>();
        services.AddHostedService(sp => sp.GetRequiredService<ArtistImageService>());
        services.AddSingleton<MatchingPipeline>();
        services.AddSingleton<ReviewService>();

        services.AddSingleton<InboxScanner>();
        services.AddHostedService(sp => sp.GetRequiredService<InboxScanner>());
        services.AddSingleton<LibraryIndexer>();
        services.AddHostedService(sp => sp.GetRequiredService<LibraryIndexer>());
        services.AddHostedService<PipelineWorker>();
        services.AddHostedService<TrashCleanupService>();

        services.AddHttpClient("webpush", c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<WebPushSender>();
        services.AddSingleton<PushNotificationService>();
        services.AddHostedService(sp => sp.GetRequiredService<PushNotificationService>());
        return services;
    }
}
