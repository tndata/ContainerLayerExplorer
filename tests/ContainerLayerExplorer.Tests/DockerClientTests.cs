using System.Diagnostics;
using System.Net;
using System.Text;
using ContainerLayerExplorer.Docker;

namespace ContainerLayerExplorer.Tests;

public class DockerClientTests
{
    private const string TestEndpoint = "npipe:////./pipe/test";

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private static DockerClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, int shortTimeoutMs = 500) =>
        new(TestEndpoint, new FakeHandler(send), TimeSpan.FromMilliseconds(shortTimeoutMs), exists: _ => true);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_missing_pipe_is_reported_at_once_with_a_clear_message()
    {
        using var client = new DockerClient("npipe:////./pipe/cle-test-" + Guid.NewGuid().ToString("N"));
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<DockerUnavailableException>(() => client.GetVersionAsync(TestContext.Current.CancellationToken));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
        Assert.Contains("Cannot reach Docker at", ex.Message);
        Assert.Contains("Is Docker Desktop running?", ex.Message);
    }

    [Fact]
    public async Task An_unsupported_endpoint_is_reported_when_Docker_is_used()
    {
        using var client = new DockerClient("tcp://localhost:2375");

        var ex = await Assert.ThrowsAsync<DockerUnavailableException>(() => client.ListImagesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("tcp://localhost:2375", ex.Message);
        Assert.Contains("not supported", ex.Message);
    }

    [Fact]
    public async Task A_failed_connection_becomes_a_clear_message()
    {
        using var client = Client((_, _) => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<DockerUnavailableException>(() => client.GetVersionAsync(TestContext.Current.CancellationToken));

        Assert.Contains($"Cannot reach Docker at {TestEndpoint}", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task A_Docker_that_does_not_answer_in_time_becomes_a_clear_message()
    {
        using var client = Client(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }, shortTimeoutMs: 100);

        var ex = await Assert.ThrowsAsync<DockerUnavailableException>(() => client.ListImagesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("did not answer within", ex.Message);
        Assert.Contains("Is Docker Desktop still starting?", ex.Message);
    }

    [Fact]
    public async Task A_cancelled_request_is_not_reported_as_a_Docker_problem()
    {
        using var client = Client(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetVersionAsync(cancelled.Token));
    }

    [Fact]
    public async Task An_unknown_image_is_null_not_an_error()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Null(await client.GetImageInfoAsync("nothing:latest", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_error_answer_from_Docker_is_not_hidden_as_unavailable()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetVersionAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task Saving_an_image_has_no_time_limit()
    {
        using var client = Client(async (_, ct) =>
        {
            await Task.Delay(300, ct); // longer than the short-call limit below
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("tar") };
        }, shortTimeoutMs: 50);

        await using var tar = await client.SaveImageAsync("demo:latest", TestContext.Current.CancellationToken);
        using var reader = new StreamReader(tar);

        Assert.Equal("tar", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_version_is_read_from_the_answer()
    {
        using var client = Client((_, _) => Task.FromResult(Json("""{"Version":"29.9.3","ApiVersion":"1.52"}""")));

        Assert.Equal("29.9.3", await client.GetVersionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Only_tagged_images_are_listed_newest_first()
    {
        using var client = Client((_, _) => Task.FromResult(Json("""
            [
              {"Id":"sha256:a","RepoTags":["old:latest"],"Size":10,"Created":100},
              {"Id":"sha256:b","RepoTags":["<none>:<none>"],"Size":1,"Created":300},
              {"Id":"sha256:c","RepoTags":["new:latest","new:v2"],"Size":20,"Created":200}
            ]
            """)));

        var images = await client.ListImagesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["sha256:c", "sha256:a"], images.Select(i => i.Id));
        Assert.Equal(["new:latest", "new:v2"], images[0].Tags);
    }
}
