using ContainerLayerExplorer.Images;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerLayerExplorer.Tests;

public class CacheFolderTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "cle-tests", "cache-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private static NullLogger Log => NullLogger.Instance;

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private string MakeFolder(string name, TimeSpan age, bool withFile = true)
    {
        var dir = Path.Combine(_base, name);
        Directory.CreateDirectory(dir);
        if (withFile) File.WriteAllText(Path.Combine(dir, "layer.tar"), "data");
        Directory.SetLastWriteTimeUtc(dir, Now - age);
        return dir;
    }

    [Fact]
    public void Each_copy_gets_its_own_folder_under_the_base()
    {
        using var first = CacheFolder.Create(_base, Log, Now);
        using var second = CacheFolder.Create(_base, Log, Now);

        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(_base, Path.GetDirectoryName(first.Path));
        Assert.True(Directory.Exists(first.Path));
        Assert.True(Directory.Exists(second.Path));
    }

    [Fact]
    public void Starting_another_copy_does_not_touch_a_running_copys_images()
    {
        using var first = CacheFolder.Create(_base, Log, Now);
        var image = Path.Combine(first.Path, "abc123");
        Directory.CreateDirectory(image);
        File.WriteAllText(Path.Combine(image, "manifest.json"), "[]");

        using var second = CacheFolder.Create(_base, Log, Now);

        Assert.True(File.Exists(Path.Combine(image, "manifest.json")));
    }

    [Fact]
    public void Folders_older_than_a_day_are_removed_with_their_contents_and_newer_ones_stay()
    {
        var old = MakeFolder("old", TimeSpan.FromHours(25));
        var recent = MakeFolder("recent", TimeSpan.FromHours(23));

        using var mine = CacheFolder.Create(_base, Log, Now);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(File.Exists(Path.Combine(recent, "layer.tar")));
    }

    [Fact]
    public void Folders_left_by_the_old_layout_without_a_lock_file_are_removed_when_old()
    {
        var leftover = MakeFolder("0123456789abcdef", TimeSpan.FromDays(3), withFile: false);

        using var mine = CacheFolder.Create(_base, Log, Now);

        Assert.False(Directory.Exists(leftover));
    }

    [Fact]
    public void An_old_folder_that_a_running_copy_still_holds_is_left_alone()
    {
        using var idleCopy = CacheFolder.Create(_base, Log, Now);
        Directory.SetLastWriteTimeUtc(idleCopy.Path, Now - TimeSpan.FromDays(5)); // idle for days, but still running

        using var newCopy = CacheFolder.Create(_base, Log, Now);

        Assert.True(Directory.Exists(idleCopy.Path));
    }

    [Fact]
    public void A_folder_whose_copy_has_stopped_is_removed_once_it_is_old()
    {
        var crashed = CacheFolder.Create(_base, Log, Now);
        var path = crashed.Path;
        // simulate a killed process: the lock is released but the folder stays
        crashed.ReleaseLock();
        Directory.SetLastWriteTimeUtc(path, Now - TimeSpan.FromDays(2));

        using var newCopy = CacheFolder.Create(_base, Log, Now);

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void Shutting_down_removes_only_this_copys_folder()
    {
        var other = MakeFolder("someone-else", TimeSpan.FromHours(1));
        var mine = CacheFolder.Create(_base, Log, Now);
        var path = mine.Path;
        File.WriteAllText(Path.Combine(path, "layer.tar"), "data");

        mine.Dispose();

        Assert.False(Directory.Exists(path));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void A_missing_base_folder_is_fine()
    {
        CacheFolder.RemoveStale(Path.Combine(_base, "nothing-here"), CacheFolder.StaleAfter, Now, Log);
        using var mine = CacheFolder.Create(Path.Combine(_base, "new"), Log, Now);

        Assert.True(Directory.Exists(mine.Path));
    }
}
