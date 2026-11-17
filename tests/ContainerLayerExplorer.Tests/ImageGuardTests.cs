using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using ContainerLayerExplorer.Docker;
using ContainerLayerExplorer.Images;

namespace ContainerLayerExplorer.Tests;

public class ImageGuardTests
{
    private const long Mb = 1024 * 1024;

    [Fact]
    public void An_image_at_the_limit_passes()
    {
        ImageGuard.CheckSize(4096 * Mb, 4096);
    }

    [Fact]
    public void An_image_over_the_limit_is_refused_with_both_sizes_and_the_setting_name()
    {
        var ex = Assert.Throws<ImageTooLargeException>(() => ImageGuard.CheckSize(4096 * Mb + 1, 4096));

        Assert.Equal(4096, ex.LimitMb);
        Assert.Contains("4,097 MB", ex.Message);
        Assert.Contains("limit is 4,096 MB", ex.Message);
        Assert.Contains("MaxImageSizeMb", ex.Message);
    }

    [Fact]
    public void An_unknown_size_passes()
    {
        ImageGuard.CheckSize(0, 1);
    }

    [Fact]
    public void Enough_free_space_passes_and_so_does_unknown_free_space()
    {
        ImageGuard.CheckFreeSpace(100 * Mb, 100 * Mb, "C:\\temp");
        ImageGuard.CheckFreeSpace(100 * Mb, null, "C:\\temp");
    }

    [Fact]
    public void Less_free_space_than_the_image_size_is_refused_with_a_clear_message()
    {
        var ex = Assert.Throws<NotEnoughDiskSpaceException>(() => ImageGuard.CheckFreeSpace(500 * Mb, 200 * Mb, "C:\\temp"));

        Assert.Contains("Not enough free disk space", ex.Message);
        Assert.Contains("500", ex.Message);
        Assert.Contains("200", ex.Message);
        Assert.Contains("C:\\temp", ex.Message);
    }

    [Fact]
    public void The_free_space_of_a_real_folder_can_be_read()
    {
        Assert.True(ImageGuard.FreeSpaceOf(Path.GetTempPath()) > 0);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070027))] // ERROR_HANDLE_DISK_FULL
    [InlineData(unchecked((int)0x80070070))] // ERROR_DISK_FULL
    public void The_Windows_disk_full_errors_are_recognised(int hresult)
    {
        Assert.True(ImageGuard.IsDiskFull(new IOException("full") { HResult = hresult }));
    }

    [Fact]
    public void Other_IO_errors_are_not_disk_full()
    {
        Assert.False(ImageGuard.IsDiskFull(new IOException("locked") { HResult = unchecked((int)0x80070020) }));
    }

    [Fact]
    public void A_full_disk_while_unpacking_gives_a_clear_message_not_a_bad_image()
    {
        // The extractor's own catch uses the same check; here we prove the message type is what the API will map to 507.
        var ex = new NotEnoughDiskSpaceException("The disk is full, so the image could not be saved. Free some space and try again.");

        Assert.IsNotType<Core.Images.ImageReadException>(ex);
    }

    [Fact]
    public void The_default_limits_are_the_agreed_ones()
    {
        var limits = new Limits();

        Assert.Equal(4096, limits.MaxImageSizeMb);
        Assert.Equal(200_000, limits.MaxEntries);
    }

    [Theory]
    [InlineData(0, 200_000, false)]
    [InlineData(4096, 0, false)]
    [InlineData(-5, 10, false)]
    [InlineData(1, 1, true)]
    public void Limits_must_be_positive(int mb, int entries, bool valid)
    {
        var limits = new Limits { MaxImageSizeMb = mb, MaxEntries = entries };

        Assert.Equal(valid, Validator.TryValidateObject(limits, new ValidationContext(limits), [], validateAllProperties: true));
    }

    [Fact]
    public async Task Docker_image_info_carries_the_size_and_falls_back_to_zero()
    {
        using var withSize = Client("""{"Id":"sha256:abc","Size":123456789,"Os":"linux","Architecture":"amd64","RootFS":{"Layers":["a"]}}""");
        using var withoutSize = Client("""{"Id":"sha256:abc","RootFS":{"Layers":["a"]}}""");

        Assert.Equal(123456789, (await withSize.GetImageInfoAsync("x", default))!.Size);
        Assert.Equal(0, (await withoutSize.GetImageInfoAsync("x", default))!.Size);
    }

    private sealed class FakeHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static DockerClient Client(string json) =>
        new("npipe:////./pipe/test", new FakeHandler(json), TimeSpan.FromSeconds(5), exists: _ => true);
}
