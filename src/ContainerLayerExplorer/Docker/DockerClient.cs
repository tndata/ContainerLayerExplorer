using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContainerLayerExplorer.Docker;

public sealed record DockerImage(string Id, IReadOnlyList<string> Tags, long Size, DateTimeOffset Created);

/// <param name="Size">Size in bytes as Docker reports it; 0 when Docker gives none.</param>
public sealed record DockerImageInfo(string Id, int LayerCount, string Os, string Architecture, long Size = 0);

public sealed record DockerEvent(string Action, string Id);

/// <summary>
/// Minimal Docker Engine API client over the local named pipe (Windows) or Unix socket.
/// Plain HTTP; no Docker.DotNet package needed. Docker Desktop is the supported engine.
/// Every way Docker can be unavailable (not running, not answering, unsupported endpoint) ends as one
/// <see cref="DockerUnavailableException"/> with a message fit for the page.
/// </summary>
public sealed class DockerClient : IDisposable
{
    /// <summary>How long the short calls (version, image list, image info) may take to start answering.
    /// Saving an image and watching events have no limit.</summary>
    public static readonly TimeSpan ShortCallTimeout = TimeSpan.FromSeconds(10);

    private const int ConnectTimeoutMs = 2000;

    private readonly HttpClient _http;
    private readonly DockerEndpoint? _endpoint;
    private readonly string? _configError;
    private readonly Func<DockerEndpoint, bool> _exists;
    private readonly TimeSpan _shortTimeout;

    public string Endpoint { get; }

    public DockerClient(string? dockerHost = null)
        : this(dockerHost ?? DetectEndpoint(), handler: null, ShortCallTimeout, exists: null)
    {
    }

    /// <param name="handler">Replaces the pipe or socket connection (tests).</param>
    /// <param name="exists">Replaces the check that the pipe or socket exists (tests).</param>
    internal DockerClient(string endpoint, HttpMessageHandler? handler, TimeSpan shortTimeout, Func<DockerEndpoint, bool>? exists)
    {
        Endpoint = endpoint;
        _shortTimeout = shortTimeout;
        _exists = exists ?? (e => e.Exists());
        try
        {
            _endpoint = DockerEndpoint.Parse(endpoint);
        }
        catch (DockerUnavailableException ex)
        {
            _configError = ex.Message; // reported when Docker is first used, not at startup
        }
        handler ??= new SocketsHttpHandler { ConnectCallback = (_, ct) => ConnectAsync(_endpoint!, ct) };
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://docker/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    private static string DetectEndpoint() => DockerEndpoint.Detect(
        Environment.GetEnvironmentVariable("DOCKER_HOST"),
        OperatingSystem.IsWindows(),
        pipe => File.Exists($@"\\.\pipe\{pipe}"));

    private static async ValueTask<Stream> ConnectAsync(DockerEndpoint endpoint, CancellationToken ct)
    {
        if (endpoint.Kind == DockerEndpointKind.NamedPipe)
        {
            var pipe = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(ConnectTimeoutMs, ct);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), ct);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new NetworkStream(socket, ownsSocket: true);
    }

    public async Task<string> GetVersionAsync(CancellationToken ct)
    {
        using var response = await SendAsync("version", _shortTimeout, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("Version", out var version) ? version.GetString() ?? "?" : "?";
    }

    /// <summary>Tagged images only; untagged (dangling) build leftovers are noise in a picker.</summary>
    public async Task<List<DockerImage>> ListImagesAsync(CancellationToken ct)
    {
        var list = await GetJsonAsync<List<ImageSummaryDto>>("images/json", ct) ?? [];
        return list
            .Select(i => new DockerImage(i.Id, (i.RepoTags ?? []).Where(t => t != "<none>:<none>").ToList(), i.Size,
                DateTimeOffset.FromUnixTimeSeconds(i.Created)))
            .Where(i => i.Tags.Count > 0)
            .OrderByDescending(i => i.Created)
            .ToList();
    }

    /// <summary>Resolves a name like "demo:latest" to its ID and layer count, or null if it does not exist.</summary>
    public async Task<DockerImageInfo?> GetImageInfoAsync(string name, CancellationToken ct)
    {
        using var response = await SendAsync($"images/{Uri.EscapeDataString(name)}/json", _shortTimeout, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;
        var layers = root.TryGetProperty("RootFS", out var rootFs) && rootFs.TryGetProperty("Layers", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.GetArrayLength()
            : 0;
        return new DockerImageInfo(
            root.GetProperty("Id").GetString()!,
            layers,
            root.TryGetProperty("Os", out var os) ? os.GetString() ?? "" : "",
            root.TryGetProperty("Architecture", out var arch) ? arch.GetString() ?? "" : "",
            root.TryGetProperty("Size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0);
    }

    /// <summary>The same tar that <c>docker save</c> writes, as a stream. No time limit: big images take a while.</summary>
    public async Task<Stream> SaveImageAsync(string name, CancellationToken ct)
    {
        var response = await SendAsync($"images/{Uri.EscapeDataString(name)}/get", timeout: null, ct);
        try
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(ct);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Image events (tag, untag, delete...) as they happen, from one connection to Docker. The first event is
    /// <c>connected</c>, sent when Docker has accepted the connection. A line Docker sends that cannot be read is skipped.
    /// The sequence ends when Docker closes the connection; Docker being unreachable or dropping the connection
    /// throws, and <see cref="EventStream"/> turns both into a "disconnected" event and a new try.
    /// </summary>
    public async IAsyncEnumerable<DockerEvent> WatchImageEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var filters = Uri.EscapeDataString("""{"type":["image"]}""");
        using var response = await SendAsync($"events?filters={filters}", timeout: null, ct);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        yield return new DockerEvent("connected", "");
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (ParseEventLine(line) is { } e) yield return e;
        }
    }

    /// <summary>One line of Docker's event stream, or null for an empty line, a line that is not an event, or damaged JSON.</summary>
    internal static DockerEvent? ParseEventLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var e = JsonSerializer.Deserialize<EventDto>(line);
            return e is null ? null : new DockerEvent(e.Action ?? "", e.Actor?.Id ?? e.Id ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(path, _shortTimeout, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct);
    }

    /// <summary>
    /// Sends a GET and returns once the headers arrive. A missing pipe is reported at once; a connection that fails
    /// or a Docker that does not start answering within <paramref name="timeout"/> becomes
    /// <see cref="DockerUnavailableException"/>. A cancelled <paramref name="ct"/> is passed on unchanged.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(string path, TimeSpan? timeout, CancellationToken ct)
    {
        EnsureAvailable();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is not null) linked.CancelAfter(timeout.Value);
        try
        {
            return await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        }
        catch (OperationCanceledException) when (timeout is not null && !ct.IsCancellationRequested)
        {
            throw new DockerUnavailableException(
                $"Docker at {Endpoint} did not answer within {timeout.Value.TotalSeconds:0} seconds. Is Docker Desktop still starting?");
        }
        catch (Exception ex) when (ex is HttpRequestException { StatusCode: null } or IOException or TimeoutException)
        {
            throw new DockerUnavailableException(NotReachableMessage(), ex);
        }
    }

    private void EnsureAvailable()
    {
        if (_configError is not null) throw new DockerUnavailableException(_configError);
        if (!_exists(_endpoint!)) throw new DockerUnavailableException(NotReachableMessage());
    }

    private string NotReachableMessage() => $"Cannot reach Docker at {Endpoint}. Is Docker Desktop running?";

    public void Dispose() => _http.Dispose();

    private sealed record ImageSummaryDto(string Id, List<string>? RepoTags, long Size, long Created);

    private sealed record EventDto(
        [property: JsonPropertyName("Action")] string? Action,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("Actor")] ActorDto? Actor);

    private sealed record ActorDto([property: JsonPropertyName("ID")] string? Id);
}
