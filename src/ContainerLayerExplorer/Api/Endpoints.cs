using System.Runtime.CompilerServices;
using ContainerLayerExplorer.Core.Images;
using ContainerLayerExplorer.Docker;
using ContainerLayerExplorer.Images;

namespace ContainerLayerExplorer.Api;

/// <summary>
/// The JSON contract the page depends on. The page never talks to Docker itself, so the same page
/// can later sit on top of an upload or an in-browser parser instead.
/// </summary>
public static class Endpoints
{
    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", async (DockerClient docker, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(new { connected = true, endpoint = docker.Endpoint, version = await docker.GetVersionAsync(ct) });
            }
            catch (Exception ex) when (ex is DockerUnavailableException or HttpRequestException or IOException or TimeoutException or OperationCanceledException)
            {
                return Results.Ok(new { connected = false, endpoint = docker.Endpoint, error = ex.Message });
            }
        });

        api.MapGet("/images", async (DockerClient docker, CancellationToken ct) =>
            Results.Ok(await docker.ListImagesAsync(ct)));

        api.MapGet("/image", async (string name, ImageCache cache, CancellationToken ct) =>
        {
            var image = await cache.LoadAsync(name, ct);
            return image is null
                ? Results.NotFound(new { error = $"No image called {name}." })
                : Results.Ok(new { id = image.Id, name = image.Name, model = image.Model });
        });

        api.MapGet("/file", (string id, int layer, string path, ImageCache cache) =>
        {
            var image = cache.Get(id);
            return image is null
                ? Results.NotFound(new { error = "Image is no longer loaded; select it again." })
                : Results.Ok(FileContentReader.Read(image.Files, image.Model, layer, path));
        });

        api.MapGet("/events", (DockerClient docker, ILoggerFactory loggers, CancellationToken ct) =>
            TypedResults.ServerSentEvents(
                EventStream.Watch(docker.WatchImageEventsAsync, EventStream.DefaultRetryDelay, loggers.CreateLogger("ContainerLayerExplorer.Events"), ct),
                eventType: "image"));
    }
}
