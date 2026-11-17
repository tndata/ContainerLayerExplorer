namespace ContainerLayerExplorer.Docker;

public enum DockerEndpointKind { NamedPipe, UnixSocket }

/// <summary>
/// Where the Docker Engine API listens on this computer: a Windows named pipe or a Unix socket.
/// Docker Desktop is the supported setup; other engines are not tested.
/// </summary>
public sealed record DockerEndpoint(DockerEndpointKind Kind, string Address)
{
    private const string PipePrefix = "npipe://";
    private const string UnixPrefix = "unix://";

    /// <summary>Reads text such as <c>npipe:////./pipe/docker_engine</c> or <c>unix:///var/run/docker.sock</c>.</summary>
    public static DockerEndpoint Parse(string text)
    {
        if (text.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = text[(text.LastIndexOf('/') + 1)..];
            if (name.Length == 0) throw new DockerUnavailableException($"The Docker endpoint {text} has no pipe name.");
            return new DockerEndpoint(DockerEndpointKind.NamedPipe, name);
        }
        if (text.StartsWith(UnixPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var path = text[UnixPrefix.Length..];
            if (path.Length == 0) throw new DockerUnavailableException($"The Docker endpoint {text} has no socket path.");
            return new DockerEndpoint(DockerEndpointKind.UnixSocket, path);
        }
        throw new DockerUnavailableException(
            $"The Docker endpoint {text} is not supported. Only npipe:// and unix:// are supported (the local Docker Desktop pipe or socket).");
    }

    /// <summary>True when the pipe or socket exists, so connecting can work. Checking costs nothing; failing to connect to a missing pipe takes seconds.</summary>
    public bool Exists() => Kind == DockerEndpointKind.NamedPipe ? File.Exists(@"\\.\pipe\" + Address) : File.Exists(Address);

    /// <summary>DOCKER_HOST wins; then Docker Desktop's Linux engine pipe; then the default pipe or socket.</summary>
    public static string Detect(string? dockerHost, bool isWindows, Func<string, bool> pipeExists)
    {
        if (!string.IsNullOrEmpty(dockerHost)) return dockerHost;
        if (!isWindows) return "unix:///var/run/docker.sock";
        foreach (var pipe in new[] { "dockerDesktopLinuxEngine", "docker_engine" })
        {
            if (pipeExists(pipe)) return $"npipe:////./pipe/{pipe}";
        }
        return "npipe:////./pipe/docker_engine";
    }
}
