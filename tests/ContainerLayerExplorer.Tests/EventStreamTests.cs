using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using ContainerLayerExplorer.Api;
using ContainerLayerExplorer.Docker;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerLayerExplorer.Tests;

public class EventStreamTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(10);

    private static DockerEvent Ev(string action) => new(action, "sha256:abc");

    private static string Actions(IEnumerable<DockerEvent> events) => string.Join(",", events.Select(e => e.Action));

    /// <summary>Reads <paramref name="count"/> events from the feed, then stops it.</summary>
    private static async Task<List<DockerEvent>> Take(Func<CancellationToken, IAsyncEnumerable<DockerEvent>> open, int count, TimeSpan? delay = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // a stuck test fails instead of hanging
        var got = new List<DockerEvent>();
        await foreach (var e in EventStream.Watch(open, delay ?? Quick, NullLogger.Instance, cts.Token))
        {
            got.Add(e);
            if (got.Count == count) break;
        }
        return got;
    }

    // ---- reading Docker's lines

    [Fact]
    public void A_good_line_is_read()
    {
        var e = DockerClient.ParseEventLine("""{"Type":"image","Action":"tag","Actor":{"ID":"sha256:abc"}}""");

        Assert.Equal(new DockerEvent("tag", "sha256:abc"), e);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ this is not json")]
    [InlineData("123")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("\"just a string\"")]
    public void A_line_that_is_not_an_event_is_skipped(string line)
    {
        Assert.Null(DockerClient.ParseEventLine(line));
    }

    private sealed class FakeHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
    }

    [Fact]
    public async Task A_bad_line_in_the_middle_does_not_end_the_stream()
    {
        const string body = """
            {"Action":"tag","Actor":{"ID":"sha256:one"}}
            {{{ damaged

            {"Action":"delete","Actor":{"ID":"sha256:two"}}
            """;
        using var docker = new DockerClient("npipe:////./pipe/test", new FakeHandler(body), TimeSpan.FromSeconds(5), _ => true);

        var got = new List<DockerEvent>();
        await foreach (var e in docker.WatchImageEventsAsync(default)) got.Add(e);

        Assert.Equal("connected,tag,delete", Actions(got));
        Assert.Equal("sha256:two", got[^1].Id);
    }

    // ---- keeping the feed going

    [Fact]
    public async Task A_dropped_connection_gives_one_disconnected_event_and_then_a_new_connection()
    {
        var attempt = 0;
        async IAsyncEnumerable<DockerEvent> Open([EnumeratorCancellation] CancellationToken ct)
        {
            if (++attempt == 1)
            {
                yield return Ev("connected");
                yield return Ev("tag");
                await Task.Yield();
                throw new IOException("the pipe was closed");
            }
            yield return Ev("connected");
            yield return Ev("untag");
            await Task.Delay(Timeout.Infinite, ct);
        }

        var got = await Take(Open, 5);

        Assert.Equal("connected,tag,disconnected,connected,untag", Actions(got));
    }

    [Fact]
    public async Task Docker_closing_the_stream_is_handled_like_a_drop()
    {
        var attempt = 0;
        async IAsyncEnumerable<DockerEvent> Open([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return Ev("connected");
            if (++attempt == 2) await Task.Delay(Timeout.Infinite, ct);
        }

        var got = await Take(Open, 3);

        Assert.Equal("connected,disconnected,connected", Actions(got));
    }

    [Fact]
    public async Task Docker_being_down_gives_only_one_disconnected_event_however_long_it_lasts_and_the_tries_go_on()
    {
        var attempts = 0;
        async IAsyncEnumerable<DockerEvent> Open([EnumeratorCancellation] CancellationToken ct)
        {
            if (++attempts < 5) throw new DockerUnavailableException("Cannot reach Docker.");
            await Task.Yield();
            yield return Ev("connected");
            await Task.Delay(Timeout.Infinite, ct);
        }

        var got = await Take(Open, 2);

        Assert.Equal("disconnected,connected", Actions(got));
        Assert.Equal(5, attempts);
    }

    [Fact]
    public async Task The_wait_between_tries_is_honoured()
    {
        async IAsyncEnumerable<DockerEvent> Open([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw new DockerUnavailableException("Cannot reach Docker.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        using var cts = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        var seen = 0;
        cts.CancelAfter(TimeSpan.FromMilliseconds(450));
        await foreach (var _ in EventStream.Watch(Open, TimeSpan.FromMilliseconds(100), NullLogger.Instance, cts.Token)) seen++;

        Assert.Equal(1, seen); // one "disconnected", then silent retries
        Assert.True(clock.ElapsedMilliseconds >= 400, $"stopped after {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Closing_the_stream_ends_the_feed_quickly_without_a_disconnected_event()
    {
        async IAsyncEnumerable<DockerEvent> Open([EnumeratorCancellation] CancellationToken ct)
        {
            yield return Ev("connected");
            await Task.Delay(Timeout.Infinite, ct);
        }

        using var cts = new CancellationTokenSource();
        var got = new List<DockerEvent>();
        var clock = Stopwatch.StartNew();
        await foreach (var e in EventStream.Watch(Open, TimeSpan.FromSeconds(30), NullLogger.Instance, cts.Token))
        {
            got.Add(e);
            cts.Cancel();
        }

        Assert.Equal("connected", Actions(got));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
    }
}
