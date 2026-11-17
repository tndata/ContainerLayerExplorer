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

    public static async IAsyncEnumerable<DockerEvent> Watch(
        Func<CancellationToken, IAsyncEnumerable<DockerEvent>> open,
        TimeSpan retryDelay,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var disconnectedSent = false;
        while (!ct.IsCancellationRequested)
        {
            string? why = null;
            var enumerator = open(ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    DockerEvent? next = null;
                    try
                    {
                        if (await enumerator.MoveNextAsync()) next = enumerator.Current;
                    }
                    catch (Exception ex) when (ex is DockerUnavailableException or HttpRequestException or IOException or OperationCanceledException)
                    {
                        why = ex.Message;
                    }

                    if (next is null) break;
                    disconnectedSent = false;
                    yield return next;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            if (ct.IsCancellationRequested) yield break; // the browser closed the stream; nothing went wrong

            logger.LogWarning("Docker event stream ended ({Why}); trying again in {Seconds} seconds.", why ?? "Docker closed it", retryDelay.TotalSeconds);
            if (!disconnectedSent)
            {
                disconnectedSent = true;
                yield return new DockerEvent("disconnected", "");
            }

            await Task.Delay(retryDelay, ct).ContinueWith(_ => { }, CancellationToken.None); // a cancelled wait ends the loop above
        }
    }
}
