using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ContainerLayerExplorer.Api;
using ContainerLayerExplorer.Docker;
using ContainerLayerExplorer.Images;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContainerLayerExplorer.Tests;

/// <summary>
/// Runs the real API in-process (Development mode, like <c>dotnet run</c>) on a free port, with Docker replaced by a fake,
/// and checks the status code and the JSON body for each kind of failure.
/// </summary>
public class ApiErrorsTests
{
    private const string Id = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private sealed class FakeHandler(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(Uri.UnescapeDataString(request.RequestUri!.AbsolutePath)));
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ImageJson(long size) =>
        Json($$$"""{"Id":"{{{Id}}}","Size":{{{size}}},"Os":"linux","Architecture":"amd64","RootFS":{"Layers":["a"]}}""");

    /// <summary>Docker answers: <c>big</c> is 5000 MB, <c>bad</c> is tiny but saves as something that is not a tar.</summary>
    private static HttpResponseMessage FakeDocker(string path) => path switch
    {
        "/version" => Json("""{"Version":"1.0"}"""),
        "/images/big:latest/json" => ImageJson(5000L * 1024 * 1024),
        "/images/bad:latest/json" => ImageJson(1000),
        "/images/" + Id + "/get" => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(Enumerable.Repeat("this is not a tar file. ", 100)))
        },
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private sealed class Server(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public static async Task<Server> StartAsync(bool dockerRunning)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var docker = new DockerClient("npipe:////./pipe/test", new FakeHandler(FakeDocker), TimeSpan.FromSeconds(5), _ => dockerRunning);
            builder.Services.AddSingleton(docker);
            builder.Services.AddSingleton<IOptions<Limits>>(Options.Create(new Limits()));
            builder.Services.AddSingleton<ImageCache>();

            var app = builder.Build();
            app.UseApiErrors();
            app.MapApi();
            app.MapGet("/api/test/boom", () => { throw new InvalidOperationException("secret detail at C:\\private\\path"); });
            app.MapGet("/api/test/disk", () => { throw new NotEnoughDiskSpaceException("The disk is full."); });
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new Server(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private static async Task<(HttpStatusCode Status, string Body, string? Error)> Get(Server server, string url)
    {
        using var response = await server.Client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType ?? "");
        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        return (response.StatusCode, body, error);
    }

    [Fact]
    public async Task Docker_not_running_gives_503_with_the_message()
    {
        await using var server = await Server.StartAsync(dockerRunning: false);

        var (status, _, error) = await Get(server, "/api/images");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("Cannot reach Docker", error);
    }

    [Fact]
    public async Task A_bad_image_gives_422_with_the_reason()
    {
        await using var server = await Server.StartAsync(dockerRunning: true);

        var (status, _, error) = await Get(server, "/api/image?name=bad:latest");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.StartsWith("This image could not be read: ", error);
    }

    [Fact]
    public async Task A_too_big_image_gives_413_with_the_sizes()
    {
        await using var server = await Server.StartAsync(dockerRunning: true);

        var (status, _, error) = await Get(server, "/api/image?name=big:latest");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Contains("5,000 MB", error);
        Assert.Contains("MaxImageSizeMb", error);
    }

    [Fact]
    public async Task A_full_disk_gives_507()
    {
        await using var server = await Server.StartAsync(dockerRunning: true);

        var (status, _, error) = await Get(server, "/api/test/disk");

        Assert.Equal(HttpStatusCode.InsufficientStorage, status);
        Assert.Equal("The disk is full.", error);
    }

    [Fact]
    public async Task An_unexpected_error_gives_500_with_a_fixed_sentence_and_no_details_even_in_Development()
    {
        await using var server = await Server.StartAsync(dockerRunning: true);

        var (status, body, error) = await Get(server, "/api/test/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(ApiErrors.UnexpectedMessage, error);
        Assert.DoesNotContain("secret detail", body);
        Assert.DoesNotContain("C:\\\\private", body);
        Assert.DoesNotContain("   at ", body);
    }

    [Fact]
    public async Task An_unknown_image_is_still_404_with_the_error_shape()
    {
        await using var server = await Server.StartAsync(dockerRunning: true);

        var (status, _, error) = await Get(server, "/api/image?name=nothing:latest");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("nothing:latest", error);
    }

    [Fact]
    public void The_mapping_covers_every_known_failure()
    {
        Assert.Equal(503, ApiErrors.Map(new DockerUnavailableException("x")).Status);
        Assert.Equal(422, ApiErrors.Map(new Core.Images.ImageReadException("x")).Status);
        Assert.Equal(413, ApiErrors.Map(new ImageTooLargeException(2L << 30, 1)).Status);
        Assert.Equal(507, ApiErrors.Map(new NotEnoughDiskSpaceException("x")).Status);
        Assert.Equal(500, ApiErrors.Map(new IOException("x")).Status);
    }
}
