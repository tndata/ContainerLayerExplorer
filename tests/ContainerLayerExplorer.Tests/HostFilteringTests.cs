using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContainerLayerExplorer.Tests;

/// <summary>
/// The tool listens on the local machine only, and a page on another website must not be able to talk to it
/// by pointing its own name at 127.0.0.1 (DNS rebinding). That is what the Host check stops.
/// The test loads the app's real <c>appsettings.json</c>.
/// </summary>
public class HostFilteringTests
{
    private static async Task<HttpStatusCode> Ask(string host)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "app-appsettings.json"), optional: false);
        await using var app = builder.Build();
        app.MapGet("/api/ping", () => "pong");
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        request.Headers.Host = host;
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Theory]
    [InlineData("localhost:5000")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:5000")]
    [InlineData("[::1]:5000")]
    public async Task The_local_names_are_accepted(string host)
    {
        Assert.Equal(HttpStatusCode.OK, await Ask(host));
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:5000")]
    [InlineData("localhost.evil.example")]
    [InlineData("192.168.1.20:5000")]
    public async Task Any_other_Host_is_refused_with_400(string host)
    {
        Assert.Equal(HttpStatusCode.BadRequest, await Ask(host));
    }
}
