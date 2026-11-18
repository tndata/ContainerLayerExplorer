namespace ContainerLayerExplorer.Core.Images;

/// <summary>Everything the UI needs about one image: metadata only, never file contents.</summary>
/// <param name="Created">The image config's <c>created</c> time, with the offset as stored (builders write UTC).</param>
/// <param name="Author">The image config's <c>author</c> (set by MAINTAINER or docker commit --author), or null.</param>
public sealed record ImageModel(
    string Os,
    string Architecture,
    ImageConfigSummary Config,
    IReadOnlyList<HistoryStep> History,
    IReadOnlyList<Layer> Layers,
    IReadOnlyList<VisibleFile> ContainerSees,
    long TotalSize,
    long VisibleSize,
    bool Truncated,
    DateTimeOffset? Created = null,
    string? Author = null);

/// <summary>The run settings from the image config's <c>config</c> object.</summary>
public sealed record ImageConfigSummary(
    IReadOnlyList<string> Env,
    IReadOnlyList<string> Cmd,
    IReadOnlyList<string> Entrypoint,
    string? WorkingDir,
    string? User,
    IReadOnlyList<string> ExposedPorts,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> Volumes,
    HealthCheck? Healthcheck,
    string? StopSignal,
    IReadOnlyList<string> Shell)
{
    /// <summary>For an image config with no <c>config</c> object.</summary>
    public static ImageConfigSummary Empty { get; } =
        new([], [], [], null, null, [], new Dictionary<string, string>(), [], null, null, []);
}

/// <summary>The HEALTHCHECK of an image. A Test of <c>["NONE"]</c> means the check is turned off.</summary>
public sealed record HealthCheck(
    IReadOnlyList<string> Test,
    TimeSpan? Interval,
    TimeSpan? Timeout,
    TimeSpan? StartPeriod,
    int? Retries);

/// <summary>One Dockerfile step from the config history. Steps like ENV or LABEL have no layer.</summary>
public sealed record HistoryStep(string CreatedBy, DateTimeOffset? Created, bool EmptyLayer, int? LayerIndex);

/// <param name="Size">Sum of the file sizes in the layer (uncompressed).</param>
/// <param name="CompressedSize">Size of the layer blob as stored (usually gzip).</param>
/// <param name="BlobDigest">The digest of the stored blob (what the manifest lists), or null when the archive does not say.</param>
/// <param name="Facts">Counts, sizes and permission facts for the layer.</param>
public sealed record Layer(
    int Index,
    string DiffId,
    string BlobPath,
    long CompressedSize,
    long Size,
    string? CreatedBy,
    IReadOnlyList<Change> Changes,
    string? BlobDigest = null,
    LayerFacts? Facts = null);

public enum ChangeKind { Added, Modified, Deleted }

public enum EntryKind { File, Directory, Symlink, Hardlink, Other }

/// <param name="WhiteoutMarker">For deletions: the raw marker in the layer, e.g. <c>.wh.big.bin</c>.</param>
/// <param name="Note">For deletions caused by a parent: why this path disappeared.</param>
public sealed record Change(
    string Path,
    ChangeKind Kind,
    EntryKind Entry,
    long Size,
    int Mode,
    string? LinkTarget,
    string? WhiteoutMarker = null,
    string? Note = null);

/// <summary>A path in the merged file system, and the layer its current version comes from.</summary>
public sealed record VisibleFile(string Path, EntryKind Entry, long Size, int Layer);
