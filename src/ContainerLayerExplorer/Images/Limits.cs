using System.ComponentModel.DataAnnotations;

namespace ContainerLayerExplorer.Images;

/// <summary>
/// Protects the computer from an image that is too big to explore. Bound to the "Limits" section of appsettings.json.
/// </summary>
public sealed class Limits
{
    public const string SectionName = "Limits";

    /// <summary>The largest image the tool will save and unpack, in MB (1 MB = 1,048,576 bytes), as Docker reports its size.</summary>
    [Range(1, 1_000_000)]
    public int MaxImageSizeMb { get; set; } = 4096;

    /// <summary>The most file entries read from one image. A bigger image is shown cut off, not refused.</summary>
    [Range(1, 100_000_000)]
    public int MaxEntries { get; set; } = 200_000;
}
