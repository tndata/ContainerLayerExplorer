using System.Runtime.CompilerServices;
using ContainerLayerExplorer.Docker;

namespace ContainerLayerExplorer.Api;

/// <summary>
/// The page's live feed: Docker's image events, kept going across outages. When Docker is not running, drops the
/// connection or closes it, the feed sends one <c>disconnected</c> event, waits, and tries again; when Docker is back
/// the connection's own <c>connected</c> event follows. A failure is logged as one line, not a stack trace.
/// </summary>
public static class EventStream
{
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>Why one connection ended, when it ended because of an error and not because Docker closed it.</summary>
    private sealed class Ending
    {
        public string? Why { get; set; }
    }

    public static async IAsyncEnumerable<DockerEvent> Watch(
        Func<CancellationToken, IAsyncEnumerable<DockerEvent>> open,
        TimeSpan retryDelay,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var disconnectedSent = false;
        while (!ct.IsCancellationRequested)
        {
            var ending = new Ending();
            await foreach (var e in OneConnection(open, ending, ct))
            {
                disconnectedSent = false;
                yield return e;
            }

            if (ct.IsCancellationRequested) yield break; // the browser closed the stream; nothing went wrong

            logger.LogWarning("Docker event stream ended ({Why}); trying again in {Seconds} seconds.", ending.Why ?? "Docker closed it", retryDelay.TotalSeconds);
            if (!disconnectedSent)
            {
                disconnectedSent = true;
                yield return new DockerEvent("disconnected", "");
            }

            await Task.Delay(retryDelay, ct).ContinueWith(_ => { }, CancellationToken.None); // a cancelled wait ends the loop above
        }
    }

    /// <summary>The events of one connection. It ends when Docker closes the connection or fails, and <paramref name="ending"/> says which.</summary>
    private static async IAsyncEnumerable<DockerEvent> OneConnection(
        Func<CancellationToken, IAsyncEnumerable<DockerEvent>> open,
        Ending ending,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var enumerator = open(ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                var next = await NextOrNull(enumerator, ending);
                if (next is null) yield break;
                yield return next;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private static async Task<DockerEvent?> NextOrNull(IAsyncEnumerator<DockerEvent> enumerator, Ending ending)
    {
        try
        {
            return await enumerator.MoveNextAsync() ? enumerator.Current : null;
        }
        catch (Exception ex) when (ex is DockerUnavailableException or HttpRequestException or IOException or OperationCanceledException)
        {
            ending.Why = ex.Message;
            return null;
        }
    }
}
