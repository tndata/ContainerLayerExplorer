namespace ContainerLayerExplorer.Images;

/// <summary>There is not enough room on the temp drive to save the image, or the drive filled up while saving.</summary>
public sealed class NotEnoughDiskSpaceException(string message, Exception? inner = null) : Exception(message, inner);
