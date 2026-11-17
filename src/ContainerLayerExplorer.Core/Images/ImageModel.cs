namespace ContainerLayerExplorer.Core.Images;

/// <summary>Everything the UI needs about one image: metadata only, never file contents.</summary>
public sealed record ImageModel(
    string Os,
    string Architecture,
    ImageConfigSummary Config,
    IReadOnlyList<HistoryStep> History,
    IReadOnlyList<Layer> Layers,
    IReadOnlyList<VisibleFile> ContainerSees,
    long TotalSize,
    long VisibleSize,
    bool Truncated);

public sealed record ImageConfigSummary(
    IReadOnlyList<string> Env,
    IReadOnlyList<string> Cmd,
    IReadOnlyList<string> Entrypoint,
    string? WorkingDir,
    string? User,
    IReadOnlyList<string> ExposedPorts);

/// <summary>One Dockerfile step from the config history. Steps like ENV or LABEL have no layer.</summary>
public sealed record HistoryStep(string CreatedBy, DateTimeOffset? Created, bool EmptyLayer, int? LayerIndex);

/// <param name="Size">Sum of the file sizes in the layer (uncompressed).</param>
/// <param name="CompressedSize">Size of the layer blob as stored (usually gzip).</param>
public sealed record Layer(
    int Index,
    string DiffId,
    string BlobPath,
    long CompressedSize,
    long Size,
    string? CreatedBy,
    IReadOnlyList<Change> Changes);

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
