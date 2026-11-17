using System.Globalization;

namespace ContainerLayerExplorer.Images;

/// <summary>The image is bigger than <see cref="Limits.MaxImageSizeMb"/>. The message is safe to show in the page.</summary>
public sealed class ImageTooLargeException(long sizeBytes, int limitMb)
    : Exception(string.Create(CultureInfo.InvariantCulture,
        $"This image is {ImageGuard.ToMb(sizeBytes):N0} MB, the limit is {limitMb:N0} MB. Change MaxImageSizeMb in appsettings.json to raise it."))
{
    public long SizeBytes { get; } = sizeBytes;
    public int LimitMb { get; } = limitMb;
}
