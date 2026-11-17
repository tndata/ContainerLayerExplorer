using ContainerLayerExplorer.Core.Images;
using ContainerLayerExplorer.Docker;
using ContainerLayerExplorer.Images;
using Microsoft.AspNetCore.Diagnostics;

namespace ContainerLayerExplorer.Api;

/// <summary>
/// One place that turns an exception in <c>/api</c> into a status code and <c>{ "error": "..." }</c>.
/// The message of a known exception is written for the person at the keyboard. Anything else gets a
/// fixed sentence: the details go to the log, never to the page, and no stack trace is sent in any
/// environment (the developer exception page only sees exceptions that get past this handler).
/// </summary>
public static class ApiErrors
{
    public const string UnexpectedMessage = "Something went wrong inside Container Layer Explorer. The details are in the console where it runs.";

    public static (int Status, string Message) Map(Exception ex) => ex switch
    {
        DockerUnavailableException => (StatusCodes.Status503ServiceUnavailable, ex.Message),
        ImageReadException => (StatusCodes.Status422UnprocessableEntity, ex.Message),
        ImageTooLargeException => (StatusCodes.Status413PayloadTooLarge, ex.Message),
        NotEnoughDiskSpaceException => (StatusCodes.Status507InsufficientStorage, ex.Message),
        _ => (StatusCodes.Status500InternalServerError, UnexpectedMessage),
    };

    /// <summary>Adds the handler to <c>/api</c> only. Call it before <c>MapApi</c>.</summary>
    public static void UseApiErrors(this WebApplication app) =>
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/api"),
            branch => branch.UseExceptionHandler(errors => errors.Run(Write)));

    private static async Task Write(HttpContext context)
    {
        var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        if (ex is null || context.Response.HasStarted) return;

        var (status, message) = Map(ex);
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ContainerLayerExplorer.Api");
        if (status == StatusCodes.Status500InternalServerError)
            logger.LogError(ex, "Unhandled error in {Path}", context.Request.Path);
        else
            logger.LogWarning("{Path} failed with {Status}: {Message}", context.Request.Path, status, message);

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }
}
