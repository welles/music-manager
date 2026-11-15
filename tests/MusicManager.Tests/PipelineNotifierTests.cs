using MusicManager.Core.Pipeline;

namespace MusicManager.Tests;

/// <summary>The app bar text: a bulk action from the UI and the pipeline worker run at the same time.</summary>
public class PipelineNotifierTests
{
    [Fact]
    public void Worker_does_not_clear_the_progress_of_a_bulk_action()
    {
        var n = new PipelineNotifier();
        n.SetBackgroundActivity("Tags aus Spotify übernehmen (1/3): A");
        n.SetActivity("Audio wird nach M4A umgewandelt: a.flac");
        Assert.Equal("Tags aus Spotify übernehmen (1/3): A · Audio wird nach M4A umgewandelt: a.flac", n.CurrentActivity);

        n.SetActivity(null);
        Assert.Equal("Tags aus Spotify übernehmen (1/3): A", n.CurrentActivity);
    }

    [Fact]
    public void Finished_bulk_action_does_not_clear_the_worker_step()
    {
        var n = new PipelineNotifier();
        n.SetBackgroundActivity("Tags aus Spotify übernehmen (3/3): C");
        n.SetActivity("Audio wird nach M4A umgewandelt: c.flac");

        n.SetBackgroundActivity(null);
        Assert.Equal("Audio wird nach M4A umgewandelt: c.flac", n.CurrentActivity);

        n.SetActivity(null);
        Assert.Null(n.CurrentActivity);
    }
}
