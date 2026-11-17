namespace ContainerLayerExplorer.Images;

/// <summary>
/// The temp folder one running copy of the tool saves images in: <c>ContainerLayerExplorer\&lt;random&gt;</c>.
/// A second copy gets its own folder, so starting it never deletes the first one's images. The folder is removed when the
/// copy shuts down properly. After a crash or a killed process it stays, and the next start removes folders that are
/// older than a day and not in use (each running copy keeps a lock file open in its folder, so a copy that has been
/// idle for days is left alone).
/// </summary>
public sealed class CacheFolder : IDisposable
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    private const string LockFileName = ".in-use";

    private readonly ILogger _logger;
    private FileStream? _lock;

    public string Path { get; }

    private CacheFolder(string path, FileStream lockFile, ILogger logger)
    {
        Path = path;
        _lock = lockFile;
        _logger = logger;
    }

    public static string DefaultBase => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ContainerLayerExplorer");

    /// <summary>Removes stale folders under <paramref name="baseDirectory"/>, then creates and locks a new folder of its own.</summary>
    public static CacheFolder Create(string baseDirectory, ILogger logger, DateTime? nowUtc = null)
    {
        RemoveStale(baseDirectory, StaleAfter, nowUtc ?? DateTime.UtcNow, logger);
        var path = System.IO.Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var lockFile = new FileStream(System.IO.Path.Combine(path, LockFileName), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        return new CacheFolder(path, lockFile, logger);
    }

    /// <summary>Deletes folders last changed more than <paramref name="maxAge"/> ago that no running copy holds. Newer folders and folders in use stay.</summary>
    public static void RemoveStale(string baseDirectory, TimeSpan maxAge, DateTime nowUtc, ILogger logger)
    {
        if (!Directory.Exists(baseDirectory)) return;
        foreach (var dir in Directory.EnumerateDirectories(baseDirectory))
        {
            try
            {
                if (nowUtc - Directory.GetLastWriteTimeUtc(dir) < maxAge) continue;
                if (IsInUse(dir)) continue;
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove the old folder {Dir}", dir);
            }
        }
    }

    private static bool IsInUse(string dir)
    {
        var lockPath = System.IO.Path.Combine(dir, LockFileName);
        if (!File.Exists(lockPath)) return false;
        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>Lets go of the lock file. Disposing does this first; tests use it to act out a copy that was killed.</summary>
    internal void ReleaseLock()
    {
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>Releases the lock and deletes the folder with everything in it.</summary>
    public void Dispose()
    {
        ReleaseLock();
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Dir}", Path);
        }
    }
}
