using Microsoft.Extensions.Options;
using PolymarketWeather.Core.Musk;
using PolymarketWeather.Core.Storage;

namespace PolymarketWeather.Collector;

/// <summary>Polls xtracker for new posts and, every few cycles, for its counting windows.</summary>
public sealed class PostsCollectorWorker(
    XTrackerClient xtracker,
    WeatherDb db,
    IOptions<CollectorOptions> options,
    ILogger<PostsCollectorWorker> logger) : PeriodicWorker(logger)
{
    private static readonly TimeSpan InitialLookback = TimeSpan.FromDays(14);
    private static readonly TimeSpan Overlap = TimeSpan.FromHours(1);
    private static readonly TimeSpan TrackingsEvery = TimeSpan.FromMinutes(10);

    private DateTimeOffset _lastTrackingsSync = DateTimeOffset.MinValue;

    protected override TimeSpan Interval => options.Value.PostsInterval;

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var handle = options.Value.PostsHandle;
        var now = DateTimeOffset.UtcNow;

        // Posts may be imported late, so re-read an overlap window behind the newest stored post.
        var last = await db.GetLastPostTimeAsync(handle, ct);
        var from = (last ?? now - InitialLookback) - Overlap;
        var posts = await xtracker.GetPostsAsync(handle, from, now.AddHours(1), ct);
        var added = await db.InsertPostsAsync(posts, now, ct);
        if (added > 0)
            logger.LogInformation("Posts: {Added} new for @{Handle}", added, handle);

        if (now - _lastTrackingsSync >= TrackingsEvery)
        {
            var trackings = await xtracker.GetTrackingsAsync(handle, ct);
            await db.UpsertTrackingsAsync(trackings, ct);
            _lastTrackingsSync = now;
        }
    }
}
