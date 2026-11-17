namespace ContainerLayerExplorer.Docker;

/// <summary>
/// Docker cannot be used: it is not running, does not answer in time, or the configured endpoint is not supported.
/// The message is written for the person at the keyboard and is safe to show in the page.
/// </summary>
public sealed class DockerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
