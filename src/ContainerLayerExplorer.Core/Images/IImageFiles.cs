namespace ContainerLayerExplorer.Core.Images;

/// <summary>
/// The files of an unpacked <c>docker save</c> archive (manifest.json, index.json, blobs/...).
/// An abstraction so the parser does not care where they live (temp folder now, browser later).
/// </summary>
public interface IImageFiles
{
    bool Exists(string path);
    Stream Open(string path);
    long Length(string path);
}

public static class ImageFilesExtensions
{
    /// <summary>Opens a file that the archive says it has. A missing file is a damaged image, not a crash.</summary>
    public static Stream OpenRequired(this IImageFiles files, string path) =>
        files.Exists(path) ? files.Open(path) : throw new ImageReadException($"the archive is missing {path}.");
}

/// <summary>An archive extracted to a folder on disk.</summary>
public sealed class DirectoryImageFiles(string root) : IImageFiles
{
    private readonly string _root = Path.GetFullPath(root);

    public bool Exists(string path) => File.Exists(Resolve(path));
    public Stream Open(string path) => File.OpenRead(Resolve(path));
    public long Length(string path) => new FileInfo(Resolve(path)).Length;

    private string Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_root, path));
        // Paths come from JSON inside the image; never let them point outside the folder.
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ImageReadException($"it refers to a path outside its own folder ({path}).");
        return full;
    }
}
