using System.Text;

namespace ContainerLayerExplorer.Core.Images;

public sealed record FileContent(string Path, int Layer, long Size, bool IsBinary, bool Truncated, string? Text, string? Message);

/// <summary>Reads one file's content from one layer, on demand. Text only, with a size cap.</summary>
public static class FileContentReader
{
    public const int DefaultMaxBytes = 1024 * 1024;

    /// <exception cref="ImageReadException">The layer is missing, damaged or cut off.</exception>
    public static FileContent Read(IImageFiles files, ImageModel model, int layer, string path, int maxBytes = DefaultMaxBytes)
    {
        if (layer < 0 || layer >= model.Layers.Count)
            return new FileContent(path, layer, 0, false, false, null, "No such layer.");

        try
        {
            using var stream = files.OpenRequired(model.Layers[layer].BlobPath);
            var found = LayerReader.FindEntry(stream, path);
            if (found is null)
                return new FileContent(path, layer, 0, false, false, null, "This layer has no file at this path.");

            var (reader, entry) = found.Value;
            using (reader)
            {
                if (entry.DataStream is null || entry.Length == 0)
                    return new FileContent(path, layer, entry.Length, false, false, "", entry.LinkName is { Length: > 0 } link ? $"Link to {link}" : null);

                var buffer = new byte[(int)Math.Min(entry.Length, maxBytes)];
                entry.DataStream.ReadExactly(buffer);
                var binary = LooksBinary(buffer);
                return new FileContent(path, layer, entry.Length, binary, entry.Length > maxBytes,
                    binary ? null : Encoding.UTF8.GetString(buffer), binary ? "Binary file: content not shown." : null);
            }
        }
        catch (ImageReadException ex)
        {
            throw new ImageReadException($"layer {layer + 1}: {ex.Reason}", ex.InnerException);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new ImageReadException($"layer {layer + 1}: the file is cut off or the layer is damaged.", ex);
        }
    }

    private static bool LooksBinary(ReadOnlySpan<byte> data)
    {
        var sample = data[..Math.Min(data.Length, 8000)];
        return sample.Contains((byte)0);
    }
}
