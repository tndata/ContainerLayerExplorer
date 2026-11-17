using System.Globalization;

namespace ContainerLayerExplorer.Images;

/// <summary>The decisions behind <see cref="Limits"/> and the disk checks, kept free of Docker and the disk so they can be tested.</summary>
public static class ImageGuard
{
    private const long BytesPerMb = 1024 * 1024;

    public static long ToMb(long bytes) => (bytes + BytesPerMb - 1) / BytesPerMb;

    /// <summary>Throws <see cref="ImageTooLargeException"/> when the image is over the limit. An unknown size (0 or less) passes.</summary>
    public static void CheckSize(long imageBytes, int maxImageSizeMb)
    {
        if (imageBytes > (long)maxImageSizeMb * BytesPerMb) throw new ImageTooLargeException(imageBytes, maxImageSizeMb);
    }

    /// <summary>
    /// Throws <see cref="NotEnoughDiskSpaceException"/> when the free space is less than the image size.
    /// Docker's size is at least what is written to disk, so this errs on the safe side. Unknown free space (null) passes.
    /// </summary>
    public static void CheckFreeSpace(long imageBytes, long? freeBytes, string folder)
    {
        if (freeBytes is { } free && free < imageBytes)
            throw new NotEnoughDiskSpaceException(
                string.Create(CultureInfo.InvariantCulture,
                    $"Not enough free disk space for this image: it needs about {ToMb(imageBytes):N0} MB in {folder} and only {free / BytesPerMb:N0} MB is free."));
    }

    /// <summary>Free bytes on the drive that holds <paramref name="folder"/>, or null when that cannot be found out.</summary>
    public static long? FreeSpaceOf(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>True for "the disk is full" (Windows ERROR_HANDLE_DISK_FULL and ERROR_DISK_FULL, Linux and macOS ENOSPC).</summary>
    public static bool IsDiskFull(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return ex.HResult == unchecked((int)0x80070027) || ex.HResult == unchecked((int)0x80070070)
            || (!OperatingSystem.IsWindows() && code == 28);
    }
}
