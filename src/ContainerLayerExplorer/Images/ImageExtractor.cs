using System.Formats.Tar;
using ContainerLayerExplorer.Core.Images;

namespace ContainerLayerExplorer.Images;

/// <summary>
/// Unpacks the tar that <c>docker save</c> produces into a folder. .NET's <see cref="TarFile"/> refuses
/// entries that would end up outside the folder; that and a damaged tar are reported as
/// <see cref="ImageReadException"/>, so a bad image never looks like a crash.
/// </summary>
public static class ImageExtractor
{
    public static async Task ExtractAsync(Stream tar, string directory, CancellationToken ct)
    {
        try
        {
            await TarFile.ExtractToDirectoryAsync(tar, directory, overwriteFiles: true, ct);
        }
        catch (IOException ex) when (ImageGuard.IsDiskFull(ex))
        {
            throw new NotEnoughDiskSpaceException("The disk is full, so the image could not be saved. Free some space and try again.", ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new ImageReadException($"the saved image could not be unpacked ({ex.Message})", ex);
        }
    }
}
