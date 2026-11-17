using ContainerLayerExplorer.Docker;

namespace ContainerLayerExplorer.Tests;

public class DockerEndpointTests
{
    [Fact]
    public void Parses_a_named_pipe()
    {
        var endpoint = DockerEndpoint.Parse("npipe:////./pipe/dockerDesktopLinuxEngine");

        Assert.Equal(DockerEndpointKind.NamedPipe, endpoint.Kind);
        Assert.Equal("dockerDesktopLinuxEngine", endpoint.Address);
    }

    [Fact]
    public void Parses_a_unix_socket()
    {
        var endpoint = DockerEndpoint.Parse("unix:///var/run/docker.sock");

        Assert.Equal(DockerEndpointKind.UnixSocket, endpoint.Kind);
        Assert.Equal("/var/run/docker.sock", endpoint.Address);
    }

    [Theory]
    [InlineData("tcp://localhost:2375")]
    [InlineData("ssh://user@host")]
    [InlineData("localhost:2375")]
    public void Rejects_an_unsupported_endpoint_with_a_clear_message(string text)
    {
        var ex = Assert.Throws<DockerUnavailableException>(() => DockerEndpoint.Parse(text));

        Assert.Contains(text, ex.Message);
        Assert.Contains("not supported", ex.Message);
    }

    [Theory]
    [InlineData("npipe://")]
    [InlineData("unix://")]
    public void Rejects_an_endpoint_without_a_name_or_path(string text)
    {
        Assert.Throws<DockerUnavailableException>(() => DockerEndpoint.Parse(text));
    }

    [Fact]
    public void DOCKER_HOST_wins_over_everything()
    {
        var text = DockerEndpoint.Detect("tcp://somewhere:2375", isWindows: true, _ => true);

        Assert.Equal("tcp://somewhere:2375", text);
    }

    [Fact]
    public void On_Windows_the_Docker_Desktop_pipe_is_preferred()
    {
        var text = DockerEndpoint.Detect(null, isWindows: true, _ => true);

        Assert.Equal("npipe:////./pipe/dockerDesktopLinuxEngine", text);
    }

    [Fact]
    public void On_Windows_the_default_pipe_is_used_when_the_Desktop_pipe_is_missing()
    {
        var text = DockerEndpoint.Detect(null, isWindows: true, pipe => pipe == "docker_engine");

        Assert.Equal("npipe:////./pipe/docker_engine", text);
    }

    [Fact]
    public void On_Windows_the_default_pipe_is_the_answer_when_no_pipe_exists()
    {
        var text = DockerEndpoint.Detect(null, isWindows: true, _ => false);

        Assert.Equal("npipe:////./pipe/docker_engine", text);
    }

    [Fact]
    public void Elsewhere_the_default_socket_is_used()
    {
        var text = DockerEndpoint.Detect(null, isWindows: false, _ => false);

        Assert.Equal("unix:///var/run/docker.sock", text);
    }
}
