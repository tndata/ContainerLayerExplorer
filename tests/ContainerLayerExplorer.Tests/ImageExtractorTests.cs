using System.Formats.Tar;
using System.Text;
using ContainerLayerExplorer.Core.Images;
using ContainerLayerExplorer.Images;

namespace ContainerLayerExplorer.Tests;

public class ImageExtractorTests
{
    private static MemoryStream Tar(params (string Name, string Content)[] files)
    {
        var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static string NewWorkFolder()
    {
        var work = Path.Combine(Path.GetTempPath(), "cle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "target"));
        return work;
    }

    [Fact]
    public async Task A_valid_tar_is_unpacked()
    {
        var work = NewWorkFolder();
        try
        {
            await ImageExtractor.ExtractAsync(Tar(("manifest.json", "[]"), ("blobs/sha256/abc", "data")), Path.Combine(work, "target"), TestContext.Current.CancellationToken);

            Assert.Equal("[]", File.ReadAllText(Path.Combine(work, "target", "manifest.json")));
            Assert.Equal("data", File.ReadAllText(Path.Combine(work, "target", "blobs", "sha256", "abc")));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task An_entry_that_would_land_outside_the_folder_is_refused_and_nothing_is_written_there()
    {
        var work = NewWorkFolder();
        try
        {
            var ex = await Assert.ThrowsAsync<ImageReadException>(() =>
                ImageExtractor.ExtractAsync(Tar(("ok.txt", "fine"), ("../escape.txt", "gotcha")), Path.Combine(work, "target"), TestContext.Current.CancellationToken));

            Assert.StartsWith("This image could not be read: ", ex.Message);
            Assert.False(File.Exists(Path.Combine(work, "escape.txt")), "the entry escaped the target folder");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Something_that_is_not_a_tar_is_reported()
    {
        var work = NewWorkFolder();
        try
        {
            using var notATar = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("this is not a tar file. ", 100))));

            var ex = await Assert.ThrowsAsync<ImageReadException>(() =>
                ImageExtractor.ExtractAsync(notATar, Path.Combine(work, "target"), TestContext.Current.CancellationToken));

            Assert.Contains("could not be unpacked", ex.Message);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task A_cancelled_unpacking_is_not_reported_as_a_bad_image()
    {
        var work = NewWorkFolder();
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ImageExtractor.ExtractAsync(Tar(("a.txt", "x")), Path.Combine(work, "target"), cancelled.Token));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
