namespace ContainerLayerExplorer.Core.Images;

/// <summary>
/// Applies layers one by one, like the overlay file system does, and records what each layer
/// changed compared to everything below it. Whiteout rules follow the OCI image spec.
/// </summary>
public sealed class LayerStack
{
    public const string WhiteoutPrefix = ".wh.";
    public const string OpaqueMarker = ".wh..wh..opq";

    private readonly Dictionary<string, VisibleFile> _visible = new(StringComparer.Ordinal);

    public IReadOnlyCollection<VisibleFile> Visible => _visible.Values;

    /// <summary>Applies one layer on top of the current state and returns its changes.</summary>
    public List<Change> Apply(int layerIndex, IEnumerable<RawEntry> entries)
    {
        var all = entries.ToList();
        var changes = new Dictionary<string, Change>(StringComparer.Ordinal);

        // Whiteouts hide content of lower layers only, so apply them before this layer's own files.
        foreach (var e in all.Where(e => IsOpaque(e.Path)))
            ApplyOpaque(Parent(e.Path), changes);
        foreach (var e in all.Where(e => IsWhiteout(e.Path) && !IsOpaque(e.Path)))
            ApplyWhiteout(e.Path, changes);

        foreach (var e in all.Where(e => !IsWhiteout(e.Path)))
        {
            // Hidden by a whiteout and written again in the same layer is a change, not delete + add.
            var existed = _visible.ContainsKey(e.Path) || changes.TryGetValue(e.Path, out var c) && c.Kind == ChangeKind.Deleted;
            changes[e.Path] = new Change(e.Path, existed ? ChangeKind.Modified : ChangeKind.Added,
                e.Entry, e.Size, e.Mode, e.LinkTarget);
            _visible[e.Path] = new VisibleFile(e.Path, e.Entry, e.Size, layerIndex);
        }

        return changes.Values.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
    }

    private void ApplyOpaque(string dir, Dictionary<string, Change> changes)
    {
        foreach (var path in Descendants(dir))
        {
            var v = _visible[path];
            _visible.Remove(path);
            changes[path] = Deleted(v, marker: null, note: $"Folder {dir} was replaced in this layer (opaque marker {OpaqueMarker}).");
        }
    }

    private void ApplyWhiteout(string markerPath, Dictionary<string, Change> changes)
    {
        var markerName = markerPath[(markerPath.LastIndexOf('/') + 1)..];
        var target = Combine(Parent(markerPath), markerName[WhiteoutPrefix.Length..]);

        if (_visible.Remove(target, out var v))
            changes[target] = Deleted(v, markerName, note: null);
        else
            changes[target] = new Change(target, ChangeKind.Deleted, EntryKind.Other, 0, 0, null, markerName,
                "Nothing to delete: no lower layer has this path.");

        foreach (var path in Descendants(target))
        {
            var child = _visible[path];
            _visible.Remove(path);
            changes[path] = Deleted(child, marker: null, note: $"Inside deleted folder {target}.");
        }
    }

    private List<string> Descendants(string dir)
    {
        var prefix = dir == "/" ? "/" : dir + "/";
        return _visible.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k != dir).ToList();
    }

    private static Change Deleted(VisibleFile v, string? marker, string? note) =>
        new(v.Path, ChangeKind.Deleted, v.Entry, 0, 0, null, marker, note);

    private static bool IsWhiteout(string path) =>
        path[(path.LastIndexOf('/') + 1)..].StartsWith(WhiteoutPrefix, StringComparison.Ordinal);

    private static bool IsOpaque(string path) => path.EndsWith("/" + OpaqueMarker, StringComparison.Ordinal);

    private static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    private static string Combine(string dir, string name) => dir == "/" ? "/" + name : dir + "/" + name;
}
