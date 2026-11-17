using System.Text;
using System.Text.Json;
using ContainerLayerExplorer.Core.Images;
using static ContainerLayerExplorer.Tests.TestImage;

namespace ContainerLayerExplorer.Tests;

/// <summary>Every way an image can be broken must end as <see cref="ImageReadException"/> with a reason, never as a crash.</summary>
public class BrokenImageTests
{
    private const string Prefix = "This image could not be read: ";

    private static TestImage Sample(string firstFile = "hello") =>
        new TestImage()
            .Layer("COPY a.txt /", File("a.txt", firstFile))
            .Layer("COPY b.txt /", File("b.txt", "two"));

    /// <summary>Hard to compress, so cutting the layer in half really cuts the data.</summary>
    private static string BigText() => string.Concat(Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid().ToString("N")));

    private static string At(TestImage image, string relative) => Path.Combine(image.Folder, relative.Replace('/', Path.DirectorySeparatorChar));

    private static (string Config, string[] Layers) LegacyManifest(TestImage image)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllBytes(At(image, "manifest.json")));
        var first = doc.RootElement[0];
        return (first.GetProperty("Config").GetString()!, first.GetProperty("Layers").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    private static void WriteManifest(TestImage image, string json) => System.IO.File.WriteAllText(At(image, "manifest.json"), json);

    private static ImageReadException ReadFails(IImageFiles files)
    {
        var ex = Assert.Throws<ImageReadException>(() => ImageReader.Read(files));
        Assert.StartsWith(Prefix, ex.Message);
        return ex;
    }

    [Fact]
    public void A_truncated_gzip_layer_is_reported_with_its_layer_number()
    {
        using var image = new TestImage().Layer("COPY big.txt /", File("big.txt", BigText()));
        var files = image.Write(Layout.Legacy);
        var layer = At(image, LegacyManifest(image).Layers[0]);
        var bytes = System.IO.File.ReadAllBytes(layer);
        System.IO.File.WriteAllBytes(layer, bytes[..(bytes.Length * 3 / 10)]);

        var ex = ReadFails(files);

        Assert.Contains("layer 1:", ex.Message);
        Assert.Contains("damaged", ex.Message);
    }

    [Fact]
    public void A_layer_that_is_not_a_tar_is_reported()
    {
        using var image = Sample();
        var files = image.Write(Layout.Legacy);
        System.IO.File.WriteAllText(At(image, LegacyManifest(image).Layers[1]), string.Concat(Enumerable.Repeat("this is not a tar archive. ", 100)));

        var ex = ReadFails(files);

        Assert.Contains("layer 2:", ex.Message);
        Assert.Contains("not a tar archive", ex.Message);
    }

    [Fact]
    public void A_zstd_layer_is_reported_as_not_supported()
    {
        using var image = Sample();
        var files = image.Write(Layout.Legacy);
        var zstd = new byte[] { 0x28, 0xb5, 0x2f, 0xfd, 0, 0, 0, 0, 0, 0, 0, 0 };
        System.IO.File.WriteAllBytes(At(image, LegacyManifest(image).Layers[0]), zstd);

        var ex = ReadFails(files);

        Assert.Contains("layer 1:", ex.Message);
        Assert.Contains("zstd", ex.Message);
        Assert.Contains("not supported", ex.Message);
    }

    [Fact]
    public void A_missing_layer_file_is_reported()
    {
        using var image = Sample();
        var files = image.Write(Layout.Legacy);
        System.IO.File.Delete(At(image, LegacyManifest(image).Layers[0]));

        var ex = ReadFails(files);

        Assert.Contains("layer 1:", ex.Message);
        Assert.Contains("the archive is missing", ex.Message);
    }

    [Fact]
    public void A_missing_config_file_is_reported()
    {
        using var image = Sample();
        var files = image.Write(Layout.Legacy);
        System.IO.File.Delete(At(image, LegacyManifest(image).Config));

        var ex = ReadFails(files);

        Assert.Contains("the archive is missing blobs/", ex.Message);
    }

    [Fact]
    public void A_manifest_without_a_config_entry_is_reported()
    {
        using var image = Sample();
        var files = image.Write(Layout.Legacy);
        WriteManifest(image, """[{"Layers":[]}]""");

        var ex = ReadFails(files);

        Assert.Contains("manifest.json has no \"Config\" entry", ex.Message);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("null")]
    public void An_empty_manifest_is_reported(string json)
    {
        using var image = Sample();
        var files = image.Write(Layout.Legacy);
        WriteManifest(image, json);

        var ex = ReadFails(files);

        Assert.Contains("manifest.json is empty", ex.Message);
    }

    [Fact]
    public void A_manifest_that_is_not_json_is_reported()
    {
        using var image = Sample();
        var files = image.Write(Layout.Legacy);
        WriteManifest(image, "this is not json");

        var ex = ReadFails(files);

        Assert.Contains("manifest.json", ex.Message);
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void A_config_that_is_not_json_is_reported()
    {
        using var image = Sample();
        var files = image.Write(Layout.Legacy);
        System.IO.File.WriteAllText(At(image, LegacyManifest(image).Config), "{ not json");

        var ex = ReadFails(files);

        Assert.Contains("the image config", ex.Message);
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void A_config_with_an_unexpected_shape_is_still_reported_as_a_bad_image()
    {
        using var image = Sample();
        var files = image.Write(Layout.Legacy);
        System.IO.File.WriteAllText(At(image, LegacyManifest(image).Config), """{"history":["not an object"],"rootfs":{}}""");

        var ex = ReadFails(files);

        Assert.Contains("unexpected content", ex.Message);
    }

    [Theory]
    [InlineData("sha256:../../escape")]
    [InlineData("sha256:a/b")]
    [InlineData("sha256:")]
    [InlineData("no-colon")]
    public void A_bad_digest_is_reported(string digest)
    {
        using var image = Sample();
        var files = image.Write(Layout.Oci);
        System.IO.File.Delete(At(image, "manifest.json")); // make the reader use index.json
        System.IO.File.WriteAllText(At(image, "index.json"),
            JsonSerializer.Serialize(new { manifests = new[] { new { mediaType = "application/vnd.oci.image.manifest.v1+json", digest } } }));

        var ex = ReadFails(files);

        Assert.Contains("bad digest", ex.Message);
    }

    [Theory]
    [InlineData("config")]
    [InlineData("layer")]
    public void A_manifest_path_that_leaves_the_image_folder_is_refused_and_never_opened(string which)
    {
        using var image = Sample();
        var files = image.Write(Layout.Legacy);
        var (config, layers) = LegacyManifest(image);
        var outside = Path.Combine(Path.GetDirectoryName(image.Folder)!, "outside-" + Guid.NewGuid().ToString("N") + ".json");
        System.IO.File.WriteAllText(outside, "{}"); // exists, so only the path check can stop the reader
        try
        {
            var escape = "../" + Path.GetFileName(outside);
            WriteManifest(image, JsonSerializer.Serialize(new[]
            {
                new { Config = which == "config" ? escape : config, Layers = which == "layer" ? new[] { escape } : layers },
            }));

            var ex = ReadFails(files);

            Assert.Contains("outside its own folder", ex.Message);
        }
        finally
        {
            System.IO.File.Delete(outside);
        }
    }

    [Fact]
    public void An_image_index_that_refers_to_itself_is_reported_instead_of_overflowing_the_stack()
    {
        using var image = Sample();
        var files = image.Write(Layout.Oci);
        System.IO.File.Delete(At(image, "manifest.json"));
        var selfRef = JsonSerializer.Serialize(new
        {
            manifests = new[] { new { mediaType = "application/vnd.oci.image.index.v1+json", digest = "sha256:aaaa" } },
        });
        System.IO.File.WriteAllText(At(image, "index.json"), selfRef);
        System.IO.File.WriteAllText(At(image, "blobs/sha256/aaaa"), selfRef);

        var ex = ReadFails(files);

        Assert.Contains("nested too deeply", ex.Message);
    }

    [Fact]
    public void An_archive_with_no_manifest_and_no_index_is_reported()
    {
        using var image = Sample();
        var files = image.Write(Layout.Legacy);
        System.IO.File.Delete(At(image, "manifest.json"));

        var ex = ReadFails(files);

        Assert.Contains("not a docker save archive", ex.Message);
    }

    [Fact]
    public void Reading_a_file_from_a_cut_off_layer_is_reported()
    {
        using var image = new TestImage().Layer("COPY big.txt /", File("big.txt", BigText()));
        var files = image.Write(Layout.Legacy);
        var model = ImageReader.Read(files);
        var layer = At(image, LegacyManifest(image).Layers[0]);
        var bytes = System.IO.File.ReadAllBytes(layer);
        System.IO.File.WriteAllBytes(layer, bytes[..(bytes.Length * 3 / 10)]);

        var ex = Assert.Throws<ImageReadException>(() => FileContentReader.Read(files, model, 0, "/big.txt"));

        Assert.Contains("layer 1:", ex.Message);
    }

    [Fact]
    public void Reading_a_file_from_a_missing_layer_file_is_reported()
    {
        using var image = Sample();
        var files = image.Write(Layout.Legacy);
        var model = ImageReader.Read(files);
        System.IO.File.Delete(At(image, LegacyManifest(image).Layers[0]));

        var ex = Assert.Throws<ImageReadException>(() => FileContentReader.Read(files, model, 0, "/a.txt"));

        Assert.Contains("the archive is missing", ex.Message);
    }

    [Fact]
    public void A_good_image_still_reads()
    {
        using var image = Sample();

        var model = image.Read(Layout.Both);

        Assert.Equal(2, model.Layers.Count);
        Assert.Equal("hello", FileContentReader.Read(image.Write(Layout.Legacy), model, 0, "/a.txt").Text);
    }
}
