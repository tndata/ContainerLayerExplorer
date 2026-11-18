using ContainerLayerExplorer.Core.Images;
using static ContainerLayerExplorer.Tests.TestImage;

namespace ContainerLayerExplorer.Tests;

public class ImageReaderTests
{
    [Theory]
    [InlineData(Layout.Legacy)]
    [InlineData(Layout.Oci)]
    [InlineData(Layout.OciNestedWithAttestation)]
    [InlineData(Layout.Both)]
    public void Reads_every_layout_the_same_way(Layout layout)
    {
        using var image = new TestImage()
            .Layer("COPY Hello1.txt /", File("Hello1.txt", "one"))
            .Layer("COPY Hello2.txt /", File("Hello2.txt", "two"));

        var model = image.Read(layout);

        Assert.Equal(2, model.Layers.Count);
        Assert.Equal("/Hello1.txt", Assert.Single(model.Layers[0].Changes).Path);
        Assert.Equal("/Hello2.txt", Assert.Single(model.Layers[1].Changes).Path);
        Assert.Equal("linux", model.Os);
        Assert.Equal(["sh"], model.Config.Cmd);
        Assert.Equal("/app", model.Config.WorkingDir);
    }

    [Theory]
    [InlineData(Layout.Legacy)]
    [InlineData(Layout.Oci)]
    public void Scratch_with_no_files_has_no_layers(Layout layout)
    {
        using var image = new TestImage().EmptyStep("LABEL purpose=demo");

        var model = image.Read(layout);

        Assert.Empty(model.Layers);
        Assert.Empty(model.ContainerSees);
        var step = Assert.Single(model.History);
        Assert.True(step.EmptyLayer);
        Assert.Null(step.LayerIndex);
    }

    [Fact]
    public void Plain_scratch_with_null_history_reads_as_empty()
    {
        using var image = new TestImage();

        var model = image.Read();

        Assert.Empty(model.Layers);
        Assert.Empty(model.History);
    }

    [Fact]
    public void History_steps_without_a_layer_are_skipped_when_mapping_to_layers()
    {
        using var image = new TestImage()
            .EmptyStep("LABEL a=1")
            .Layer("COPY a /", File("a", "a"))
            .EmptyStep("ENV X=1")
            .Layer("COPY b /", File("b", "b"));

        var model = image.Read();

        Assert.Equal([null, 0, null, 1], model.History.Select(h => h.LayerIndex));
        Assert.Equal("COPY a /", model.Layers[0].CreatedBy);
        Assert.Equal("COPY b /", model.Layers[1].CreatedBy);
    }

    [Fact]
    public void Plain_uncompressed_layers_are_read()
    {
        using var image = new TestImage { Gzip = false }.Layer("COPY a /", File("a.txt", "hello"));

        var model = image.Read();

        Assert.Equal(5, model.Layers[0].Size);
    }

    [Fact]
    public void Overwrite_is_added_then_modified_and_both_versions_stay_in_the_image()
    {
        using var image = new TestImage()
            .Layer("COPY Hello1.txt /", File("Hello1.txt", "old text"))
            .Layer("COPY Hello3.txt /Hello1.txt", File("Hello1.txt", "new"));

        var model = image.Read();

        Assert.Equal(ChangeKind.Added, model.Layers[0].Changes[0].Kind);
        Assert.Equal(ChangeKind.Modified, model.Layers[1].Changes[0].Kind);
        Assert.Equal(8 + 3, model.TotalSize);
        Assert.Equal(3, model.VisibleSize);
        Assert.Equal(1, Assert.Single(model.ContainerSees).Layer);
    }

    [Fact]
    public void Whiteout_deletes_but_the_data_stays_in_the_lower_layer()
    {
        using var image = new TestImage()
            .Layer("COPY SecretFile.txt /", File("SecretFile.txt", "secret"))
            .Layer("RUN rm /SecretFile.txt", Whiteout("/", "SecretFile.txt"), Dir("proc"));

        var model = image.Read();

        var delete = model.Layers[1].Changes.Single(c => c.Path == "/SecretFile.txt");
        Assert.Equal(ChangeKind.Deleted, delete.Kind);
        Assert.Equal(".wh.SecretFile.txt", delete.WhiteoutMarker);
        Assert.Equal(EntryKind.File, delete.Entry);
        Assert.DoesNotContain(model.ContainerSees, v => v.Path == "/SecretFile.txt");
        Assert.Equal(6, model.TotalSize);
        Assert.Equal(0, model.VisibleSize);
    }

    [Fact]
    public void Rename_is_a_delete_plus_an_add_in_the_same_layer()
    {
        using var image = new TestImage()
            .Layer("RUN echo", File("echofile.txt", "Hi"))
            .Layer("RUN mv", Whiteout("/", "echofile.txt"), File("renamed.txt", "Hi"));

        var changes = image.Read().Layers[1].Changes;

        Assert.Equal(ChangeKind.Deleted, changes.Single(c => c.Path == "/echofile.txt").Kind);
        Assert.Equal(ChangeKind.Added, changes.Single(c => c.Path == "/renamed.txt").Kind);
    }

    [Fact]
    public void Deleting_a_folder_hides_everything_inside_it()
    {
        using var image = new TestImage()
            .Layer("COPY data", Dir("data"), File("data/a.txt", "a"), Dir("data/sub"), File("data/sub/b.txt", "b"), File("keep.txt", "k"))
            .Layer("RUN rm -rf /data", Whiteout("/", "data"));

        var model = image.Read();

        var deleted = model.Layers[1].Changes.Where(c => c.Kind == ChangeKind.Deleted).Select(c => c.Path);
        Assert.Equal(["/data", "/data/a.txt", "/data/sub", "/data/sub/b.txt"], deleted);
        Assert.Equal("/keep.txt", Assert.Single(model.ContainerSees).Path);
    }

    [Fact]
    public void Opaque_folder_hides_lower_content_but_keeps_files_written_in_the_same_layer()
    {
        using var image = new TestImage()
            .Layer("COPY", Dir("d"), File("d/old.txt", "old"), File("d/same.txt", "v1"))
            .Layer("RUN rm -rf /d && mkdir /d && ...", Dir("d"), Opaque("d"), File("d/same.txt", "v2"), File("d/new.txt", "n"));

        var model = image.Read();
        var changes = model.Layers[1].Changes.ToDictionary(c => c.Path, c => c.Kind);

        Assert.Equal(ChangeKind.Deleted, changes["/d/old.txt"]);
        Assert.Equal(ChangeKind.Modified, changes["/d/same.txt"]);
        Assert.Equal(ChangeKind.Added, changes["/d/new.txt"]);
        Assert.Equal(ChangeKind.Modified, changes["/d"]);
        Assert.Equal(["/d", "/d/new.txt", "/d/same.txt"], model.ContainerSees.Select(v => v.Path));
    }

    [Fact]
    public void Entry_cap_marks_the_model_as_truncated()
    {
        using var image = new TestImage().Layer("COPY", File("a", "1"), File("b", "2"), File("c", "3"));

        var model = ImageReader.Read(image.Write(), new ImageReadOptions(MaxEntries: 2));

        Assert.True(model.Truncated);
        Assert.Equal(2, model.Layers[0].Changes.Count);
    }

    [Fact]
    public void Symlinks_keep_their_target()
    {
        using var image = new TestImage().Layer("FROM busybox", Dir("bin"), File("bin/busybox", "x"), Symlink("bin/ls", "busybox"));

        var link = image.Read().Layers[0].Changes.Single(c => c.Path == "/bin/ls");

        Assert.Equal(EntryKind.Symlink, link.Entry);
        Assert.Equal("busybox", link.LinkTarget);
    }

    [Theory]
    [InlineData(Layout.Legacy)]
    [InlineData(Layout.Oci)]
    public void Reads_the_image_created_time_and_author(Layout layout)
    {
        using var image = new TestImage
        {
            ImageFields = { ["created"] = "2026-11-05T07:17:53.714313256Z", ["author"] = "Example Maintainer <maintainer@example.com>" },
        }.Layer("COPY a /", File("a", "a"));

        var model = image.Read(layout);

        // Nine digits of fractions, as real images have; .NET keeps seven.
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 7, 17, 53, TimeSpan.Zero).AddTicks(7_143_132), model.Created);
        Assert.Equal(TimeSpan.Zero, model.Created!.Value.Offset);
        Assert.Equal("Example Maintainer <maintainer@example.com>", model.Author);
    }

    [Fact]
    public void Missing_or_odd_created_and_author_read_as_null()
    {
        using var plain = new TestImage().Layer("COPY a /", File("a", "a"));
        using var odd = new TestImage { ImageFields = { ["created"] = 12345, ["author"] = "" } }.Layer("COPY a /", File("a", "a"));

        var a = plain.Read();
        var b = odd.Read();

        Assert.Null(a.Created);
        Assert.Null(a.Author);
        Assert.Null(b.Created);
        Assert.Null(b.Author);
    }

    [Fact]
    public void Reads_labels_volumes_stop_signal_and_shell()
    {
        using var image = new TestImage
        {
            ConfigFields =
            {
                ["Labels"] = new Dictionary<string, object>
                {
                    ["org.opencontainers.image.version"] = "1.2.3",
                    ["org.opencontainers.image.source"] = "https://example.com/repo",
                    ["odd"] = 7,
                },
                ["Volumes"] = new Dictionary<string, object> { ["/data"] = new { }, ["/logs"] = new { } },
                ["StopSignal"] = "SIGQUIT",
                ["Shell"] = new[] { "/bin/bash", "-c" },
                ["ExposedPorts"] = new Dictionary<string, object> { ["80/tcp"] = new { } },
            },
        }.Layer("COPY a /", File("a", "a"));

        var config = image.Read().Config;

        Assert.Equal(["odd", "org.opencontainers.image.source", "org.opencontainers.image.version"], config.Labels.Keys);
        Assert.Equal("1.2.3", config.Labels["org.opencontainers.image.version"]);
        Assert.Equal("7", config.Labels["odd"]);   // not a string in the JSON: kept as its JSON text
        Assert.Equal(["/data", "/logs"], config.Volumes);
        Assert.Equal("SIGQUIT", config.StopSignal);
        Assert.Equal(["/bin/bash", "-c"], config.Shell);
        Assert.Equal(["80/tcp"], config.ExposedPorts);
        Assert.Null(config.Healthcheck);
    }

    [Fact]
    public void An_image_without_these_settings_reads_as_empty()
    {
        using var image = new TestImage { ConfigFields = { ["Labels"] = null, ["Volumes"] = null } }.Layer("COPY a /", File("a", "a"));

        var config = image.Read().Config;

        Assert.Empty(config.Labels);
        Assert.Empty(config.Volumes);
        Assert.Empty(config.Shell);
        Assert.Null(config.StopSignal);
        Assert.Null(config.Healthcheck);
    }

    [Fact]
    public void Reads_the_health_check_with_its_durations_in_nanoseconds()
    {
        using var image = new TestImage
        {
            ConfigFields =
            {
                ["Healthcheck"] = new Dictionary<string, object>
                {
                    ["Test"] = new[] { "CMD-SHELL", "wget -q -O /dev/null http://localhost/ || exit 1" },
                    ["Interval"] = 30_000_000_000L,
                    ["Timeout"] = 5_000_000_000L,
                    ["StartPeriod"] = 0L,   // 0 means "the default"
                    ["Retries"] = 3,
                },
            },
        }.Layer("COPY a /", File("a", "a"));

        var check = image.Read().Config.Healthcheck;

        Assert.NotNull(check);
        Assert.Equal(["CMD-SHELL", "wget -q -O /dev/null http://localhost/ || exit 1"], check.Test);
        Assert.Equal(TimeSpan.FromSeconds(30), check.Interval);
        Assert.Equal(TimeSpan.FromSeconds(5), check.Timeout);
        Assert.Null(check.StartPeriod);
        Assert.Equal(3, check.Retries);
    }

    [Fact]
    public void A_health_check_turned_off_is_kept_as_NONE()
    {
        using var image = new TestImage { ConfigFields = { ["Healthcheck"] = new { Test = new[] { "NONE" } } } }
            .Layer("COPY a /", File("a", "a"));

        var check = image.Read().Config.Healthcheck;

        Assert.NotNull(check);
        Assert.Equal(["NONE"], check.Test);
        Assert.Null(check.Interval);
        Assert.Null(check.Retries);
    }

    [Theory]
    [InlineData("./etc/passwd", "/etc/passwd")]
    [InlineData("etc/", "/etc")]
    [InlineData("/x", "/x")]
    [InlineData("./", null)]
    public void Paths_are_normalized(string raw, string? expected) =>
        Assert.Equal(expected, LayerReader.NormalizePath(raw));
}
