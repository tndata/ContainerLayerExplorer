namespace ContainerLayerExplorer.Core.Images;

/// <summary>
/// Figures about one layer, worked out from the changes of all layers. Metadata only, no file contents.
/// </summary>
/// <param name="AddedSize">Bytes of the files this layer adds.</param>
/// <param name="ModifiedSize">Bytes of the new versions of the files this layer changes (the whole file is stored again).</param>
/// <param name="WhiteoutMarkers">Deletions done with a <c>.wh.</c> marker; the rest are paths inside a deleted or replaced folder.</param>
/// <param name="DeletedSize">Bytes of lower-layer files that this layer deletes. They stay stored in the lower layer.</param>
/// <param name="HiddenLaterSize">Bytes of this layer's files that a later layer changes or deletes, so no container sees them.</param>
/// <param name="Biggest">The largest files this layer writes, biggest first (at most <see cref="MaxListed"/>).</param>
/// <param name="Executables">Files with an execute bit set.</param>
/// <param name="SetIdFiles">Files with the setuid or setgid bit (at most <see cref="MaxListed"/> listed; see the count).</param>
/// <param name="WorldWritable">Files and folders anyone may write to (at most <see cref="MaxListed"/> listed; see the count).</param>
public sealed record LayerFacts(
    int Added,
    long AddedSize,
    int Modified,
    long ModifiedSize,
    int Deleted,
    int WhiteoutMarkers,
    long DeletedSize,
    int Files,
    int Folders,
    int Symlinks,
    int OtherEntries,
    int HiddenLaterFiles,
    long HiddenLaterSize,
    IReadOnlyList<SizedPath> Biggest,
    int Executables,
    int SetIdCount,
    IReadOnlyList<ModePath> SetIdFiles,
    int WorldWritableCount,
    IReadOnlyList<ModePath> WorldWritable)
{
    public const int MaxListed = 10;
    public const int MaxBiggest = 5;

    private const int SetUid = 0x800;        // 04000
    private const int SetGid = 0x400;        // 02000
    private const int OthersWrite = 0x2;     // 00002
    private const int AnyExecute = 0x49;     // 00111

    /// <summary>One <see cref="LayerFacts"/> per layer, in layer order.</summary>
    /// <param name="visible">The merged file system after the last layer: tells which layer each visible path comes from.</param>
    public static List<LayerFacts> Compute(IReadOnlyList<Layer> layers, IEnumerable<VisibleFile> visible)
    {
        var shownFrom = visible.ToDictionary(v => v.Path, v => v.Layer, StringComparer.Ordinal);
        var lastSize = new Dictionary<string, long>(StringComparer.Ordinal);   // newest stored size of each file so far
        var result = new List<LayerFacts>();
        foreach (var layer in layers)
        {
            result.Add(ForLayer(layer, shownFrom, lastSize));
            foreach (var c in layer.Changes)
            {
                if (c.Kind == ChangeKind.Deleted) lastSize.Remove(c.Path);
                else if (c.Entry == EntryKind.File) lastSize[c.Path] = c.Size;
                else lastSize.Remove(c.Path);
            }
        }
        return result;
    }

    private static LayerFacts ForLayer(Layer layer, Dictionary<string, int> shownFrom, Dictionary<string, long> lastSize)
    {
        var written = layer.Changes.Where(c => c.Kind != ChangeKind.Deleted).ToList();
        var deleted = layer.Changes.Where(c => c.Kind == ChangeKind.Deleted).ToList();
        var files = written.Where(c => c.Entry == EntryKind.File).ToList();
        var hiddenLater = files.Where(c => !shownFrom.TryGetValue(c.Path, out var from) || from != layer.Index).ToList();
        var setId = written.Where(c => c.Entry == EntryKind.File && (c.Mode & (SetUid | SetGid)) != 0).ToList();
        // A symlink always has mode 0777; that says nothing about who can write to the target.
        var worldWritable = written.Where(c => c.Entry is EntryKind.File or EntryKind.Directory && (c.Mode & OthersWrite) != 0).ToList();

        return new LayerFacts(
            files.Count(c => c.Kind == ChangeKind.Added),
            files.Where(c => c.Kind == ChangeKind.Added).Sum(c => c.Size),
            files.Count(c => c.Kind == ChangeKind.Modified),
            files.Where(c => c.Kind == ChangeKind.Modified).Sum(c => c.Size),
            deleted.Count,
            deleted.Count(c => c.WhiteoutMarker is not null),
            deleted.Sum(c => lastSize.GetValueOrDefault(c.Path)),
            files.Count,
            written.Count(c => c.Entry == EntryKind.Directory),
            written.Count(c => c.Entry == EntryKind.Symlink),
            written.Count(c => c.Entry is EntryKind.Hardlink or EntryKind.Other),
            hiddenLater.Count,
            hiddenLater.Sum(c => c.Size),
            files.Where(c => c.Size > 0).OrderByDescending(c => c.Size).ThenBy(c => c.Path, StringComparer.Ordinal)
                .Take(MaxBiggest).Select(c => new SizedPath(c.Path, c.Size)).ToList(),
            files.Count(c => (c.Mode & AnyExecute) != 0),
            setId.Count,
            setId.Take(MaxListed).Select(c => new ModePath(c.Path, c.Mode)).ToList(),
            worldWritable.Count,
            worldWritable.Take(MaxListed).Select(c => new ModePath(c.Path, c.Mode)).ToList());
    }
}

public sealed record SizedPath(string Path, long Size);

/// <summary>A path with its permission bits as stored in the layer tar (for example 0o4755 for a setuid program).</summary>
public sealed record ModePath(string Path, int Mode);
