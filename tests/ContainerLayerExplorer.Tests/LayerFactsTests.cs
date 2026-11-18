using ContainerLayerExplorer.Core.Images;
using static ContainerLayerExplorer.Tests.TestImage;

namespace ContainerLayerExplorer.Tests;

public class LayerFactsTests
{
    [Fact]
    public void Counts_and_sizes_of_added_changed_and_deleted_files()
    {
        using var image = new TestImage()
            .Layer("COPY", Dir("src"), File("src/a.txt", "12345"), File("src/b.txt", "1234567890"), Symlink("src/link", "a.txt"))
            .Layer("COPY a again", File("src/a.txt", "123"))
            .Layer("RUN rm b", Whiteout("/src", "b.txt"));

        var facts = image.Read().Layers.Select(l => l.Facts!).ToList();

        Assert.Equal((2, 15L, 0, 0L, 0), (facts[0].Added, facts[0].AddedSize, facts[0].Modified, facts[0].ModifiedSize, facts[0].Deleted));
        Assert.Equal((2, 1, 1), (facts[0].Files, facts[0].Folders, facts[0].Symlinks));
        Assert.Equal((0, 0L, 1, 3L), (facts[1].Added, facts[1].AddedSize, facts[1].Modified, facts[1].ModifiedSize));
        Assert.Equal((1, 1, 10L), (facts[2].Deleted, facts[2].WhiteoutMarkers, facts[2].DeletedSize));
        Assert.Equal(0, facts[2].Files);   // the whiteout marker is not counted as a file
    }

    [Fact]
    public void Hidden_later_is_the_data_of_this_layer_that_a_later_layer_overwrites_or_deletes()
    {
        using var image = new TestImage()
            .Layer("COPY", File("a.txt", "12345"), File("b.txt", "1234567890"), File("keep.txt", "k"))
            .Layer("COPY a again", File("a.txt", "123"))
            .Layer("RUN rm b", Whiteout("/", "b.txt"));

        var model = image.Read();
        var facts = model.Layers.Select(l => l.Facts!).ToList();

        Assert.Equal((2, 15L), (facts[0].HiddenLaterFiles, facts[0].HiddenLaterSize));
        Assert.Equal((0, 0L), (facts[1].HiddenLaterFiles, facts[1].HiddenLaterSize));
        // Over all layers, it adds up to the image's "stored but hidden" figure.
        Assert.Equal(model.TotalSize - model.VisibleSize, facts.Sum(f => f.HiddenLaterSize));
    }

    [Fact]
    public void Paths_inside_a_deleted_folder_count_as_deleted_without_a_marker_of_their_own()
    {
        using var image = new TestImage()
            .Layer("COPY data", Dir("data"), File("data/a.txt", "aa"), File("data/b.txt", "bbb"))
            .Layer("RUN rm -rf /data", Whiteout("/", "data"));

        var facts = image.Read().Layers[1].Facts!;

        Assert.Equal(3, facts.Deleted);
        Assert.Equal(1, facts.WhiteoutMarkers);
        Assert.Equal(5, facts.DeletedSize);
    }

    [Fact]
    public void Lists_the_biggest_files_biggest_first()
    {
        using var image = new TestImage().Layer("COPY",
            File("s.txt", "1"), File("xl.txt", "1234567"), File("m.txt", "123"), File("empty.txt", ""),
            File("l.txt", "12345"), File("xs.txt", "12"), File("m2.txt", "123"));

        var biggest = image.Read().Layers[0].Facts!.Biggest;

        Assert.Equal(["/xl.txt", "/l.txt", "/m.txt", "/m2.txt", "/xs.txt"], biggest.Select(b => b.Path));
        Assert.Equal(7, biggest[0].Size);
    }

    [Fact]
    public void Permission_facts_come_from_the_mode_bits()
    {
        using var image = new TestImage().Layer("RUN install",
            File("bin/tool", "x", 755),
            File("bin/su", "x", 4755),
            File("bin/wall", "x", 2755),
            File("open.txt", "x", 666),
            Dir("tmp", 1777),
            File("plain.txt", "x", 644),
            Symlink("bin/link", "tool"));   // a symlink is 0777 but is not counted as world-writable

        var facts = image.Read().Layers[0].Facts!;

        Assert.Equal(3, facts.Executables);
        Assert.Equal(2, facts.SetIdCount);
        Assert.Equal(["/bin/su", "/bin/wall"], facts.SetIdFiles.Select(f => f.Path));
        Assert.Equal(Convert.ToInt32("4755", 8), facts.SetIdFiles[0].Mode);
        Assert.Equal(2, facts.WorldWritableCount);
        Assert.Equal(["/open.txt", "/tmp"], facts.WorldWritable.Select(f => f.Path));
    }

    [Theory]
    [InlineData(TestImage.Layout.Legacy)]
    [InlineData(TestImage.Layout.Oci)]
    public void The_blob_digest_is_the_name_of_the_stored_blob(TestImage.Layout layout)
    {
        using var image = new TestImage().Layer("COPY", File("a", "a"));

        var layer = image.Read(layout).Layers[0];

        Assert.Equal("sha256:" + Path.GetFileName(layer.BlobPath), layer.BlobDigest);
        Assert.NotEqual(layer.DiffId, layer.BlobDigest);   // gzip: the stored blob differs from the content
    }
}
