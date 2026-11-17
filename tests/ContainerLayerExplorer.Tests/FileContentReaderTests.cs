using ContainerLayerExplorer.Core.Images;
using static ContainerLayerExplorer.Tests.TestImage;

namespace ContainerLayerExplorer.Tests;

public class FileContentReaderTests
{
    [Fact]
    public void Reads_each_version_from_its_own_layer()
    {
        using var image = new TestImage()
            .Layer("COPY", File("Hello1.txt", "This is Hello1!"))
            .Layer("COPY", File("Hello1.txt", "This is Hello3!"));
        var files = image.Write();
        var model = ImageReader.Read(files);

        Assert.Equal("This is Hello1!", FileContentReader.Read(files, model, 0, "/Hello1.txt").Text);
        Assert.Equal("This is Hello3!", FileContentReader.Read(files, model, 1, "/Hello1.txt").Text);
    }

    [Fact]
    public void Truncates_large_files()
    {
        using var image = new TestImage().Layer("COPY", File("big.txt", new string('x', 100)));
        var files = image.Write();

        var content = FileContentReader.Read(files, ImageReader.Read(files), 0, "/big.txt", maxBytes: 10);

        Assert.True(content.Truncated);
        Assert.Equal(10, content.Text!.Length);
        Assert.Equal(100, content.Size);
    }

    [Fact]
    public void Binary_files_are_not_returned_as_text()
    {
        using var image = new TestImage().Layer("COPY", File("bin.dat", "a\0b"));
        var files = image.Write();

        var content = FileContentReader.Read(files, ImageReader.Read(files), 0, "/bin.dat");

        Assert.True(content.IsBinary);
        Assert.Null(content.Text);
    }

    [Fact]
    public void Missing_path_gives_a_message()
    {
        using var image = new TestImage().Layer("COPY", File("a.txt", "a"));
        var files = image.Write();

        var content = FileContentReader.Read(files, ImageReader.Read(files), 0, "/nope.txt");

        Assert.NotNull(content.Message);
        Assert.Null(content.Text);
    }
}
