using System.Text.Json.Serialization;
using ContainerLayerExplorer.Api;
using ContainerLayerExplorer.Docker;
using ContainerLayerExplorer.Images;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(_ => new DockerClient(builder.Configuration["DockerHost"]));
builder.Services.AddOptions<Limits>()
    .Bind(builder.Configuration.GetSection(Limits.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<ImageCache>();

var app = builder.Build();

app.UseApiErrors();
app.UseDefaultFiles();
// Always revalidate: the page files change while the tool is being developed, and they are tiny.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});
app.MapApi();

await app.RunAsync();
