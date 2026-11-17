namespace ContainerLayerExplorer.Core.Images;

/// <summary>
/// The image cannot be read: a file is missing or damaged, the format is not supported, or the archive
/// points outside its own folder. <see cref="Reason"/> is a short sentence fragment, and the message
/// ("This image could not be read: ...") is written for the person at the keyboard.
/// </summary>
public sealed class ImageReadException(string reason, Exception? inner = null)
    : Exception("This image could not be read: " + reason, inner)
{
    public string Reason { get; } = reason;
}
