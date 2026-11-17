using System.Formats.Tar;
using System.IO.Compression;

namespace ContainerLayerExplorer.Core.Images;

/// <summary>One entry in a layer tar, as stored (whiteout markers included).</summary>
public sealed record RawEntry(string Path, EntryKind Entry, long Size, int Mode, string? LinkTarget);

/// <summary>
/// Reads layer tars (gzip or plain). Metadata only, file data is skipped.
/// A damaged or unsupported layer ends as <see cref="ImageReadException"/>; the reason has no subject
/// ("it is ..."), because the caller adds which layer it was.
/// </summary>
public static class LayerReader
{
    public static IEnumerable<RawEntry> ReadEntries(Stream layer)
    {
        using var reader = new TarReader(Decompress(layer));
        while (Next(reader) is { } entry)
        {
            var path = NormalizePath(entry.Name);
            if (path is null) continue;
            yield return new RawEntry(path, KindOf(entry.EntryType), entry.Length, (int)entry.Mode,
                string.IsNullOrEmpty(entry.LinkName) ? null : entry.LinkName);
        }
    }

    /// <summary>Returns the data stream of one file in the layer, or null. The caller reads it before moving on.</summary>
    public static (TarReader Reader, TarEntry Entry)? FindEntry(Stream layer, string path)
    {
        var reader = new TarReader(Decompress(layer));
        try
        {
            while (Next(reader) is { } entry)
            {
                if (NormalizePath(entry.Name) == path) return (reader, entry);
            }
        }
        catch
        {
            reader.Dispose();
            throw;
        }
        reader.Dispose();
        return null;
    }

    /// <summary>"./etc/passwd", "etc/", "/x" all become "/etc/passwd", "/etc", "/x". The root itself gives null.</summary>
    public static string? NormalizePath(string name)
    {
        var p = name.Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        p = p.Trim('/');
        if (p is "" or ".") return null;
        return "/" + p;
    }

    private static TarEntry? Next(TarReader reader)
    {
        try
        {
            return reader.GetNextEntry(copyData: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new ImageReadException("it is damaged, incomplete or not a tar archive.", ex);
        }
    }

    private static Stream Decompress(Stream layer)
    {
        var buffered = new BufferedStream(layer);
        Span<byte> magic = stackalloc byte[4];
        var read = PeekBytes(buffered, magic);
        if (read >= 2 && magic[0] == 0x1f && magic[1] == 0x8b)
            return new GZipStream(buffered, CompressionMode.Decompress);
        if (read == 4 && magic[0] == 0x28 && magic[1] == 0xb5 && magic[2] == 0x2f && magic[3] == 0xfd)
            throw new ImageReadException("it is compressed with zstd, which is not supported yet.");
        return buffered;
    }

    // BufferedStream has no peek; the layer streams we get from disk are seekable, so read and rewind.
    private static int PeekBytes(Stream stream, Span<byte> buffer)
    {
        if (!stream.CanSeek) throw new NotSupportedException("Layer stream must be seekable.");
        var start = stream.Position;
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        stream.Position = start;
        return total;
    }

    private static EntryKind KindOf(TarEntryType type) => type switch
    {
        TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => EntryKind.File,
        TarEntryType.Directory => EntryKind.Directory,
        TarEntryType.SymbolicLink => EntryKind.Symlink,
        TarEntryType.HardLink => EntryKind.Hardlink,
        _ => EntryKind.Other,
    };
}
