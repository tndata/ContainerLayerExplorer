using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContainerLayerExplorer.Core.Images;

namespace ContainerLayerExplorer.Tests;

/// <summary>Builds small fake <c>docker save</c> folders in either layout, so tests need no Docker.</summary>
public sealed class TestImage : IDisposable
{
    public enum Layout { Legacy, Oci, OciNestedWithAttestation, Both }

    // Shared, never changed: the same values go into every image the tests build.
    private static readonly string[] DefaultEnv = ["PATH=/bin"];
    private static readonly string[] DefaultCmd = ["sh"];
    private static readonly string[] DefaultRepoTags = ["test:latest"];

    private readonly List<(string CreatedBy, TestEntry[]? Entries)> _steps = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cle-tests", Guid.NewGuid().ToString("N"));

    public bool Gzip { get; init; } = true;

    /// <summary>Extra top-level fields of the image config, such as <c>created</c> or <c>author</c>.</summary>
    public Dictionary<string, object?> ImageFields { get; init; } = [];

    /// <summary>Extra fields of the config's <c>config</c> object, such as <c>Labels</c> or <c>Healthcheck</c>.</summary>
    public Dictionary<string, object?> ConfigFields { get; init; } = [];

    /// <summary>The <c>created</c> time of each history step, in step order. Steps past the end get none.</summary>
    public string[] StepTimes { get; init; } = [];

    /// <summary>The folder the image is written to, so a test can damage a file after <see cref="Write"/>.</summary>
    public string Folder => _dir;

    public TestImage Layer(string createdBy, params TestEntry[] entries) { _steps.Add((createdBy, entries)); return this; }
    public TestImage EmptyStep(string createdBy) { _steps.Add((createdBy, null)); return this; }

    public static TestEntry File(string name, string content) => new(name, TarEntryType.RegularFile, content);
    public static TestEntry File(string name, string content, int octalMode) =>
        new(name, TarEntryType.RegularFile, content, Mode: (UnixFileMode)Convert.ToInt32(octalMode.ToString(), 8));
    public static TestEntry Dir(string name, int octalMode) =>
        new(name, TarEntryType.Directory, null, Mode: (UnixFileMode)Convert.ToInt32(octalMode.ToString(), 8));
    public static TestEntry Dir(string name) => new(name, TarEntryType.Directory, null);
    public static TestEntry Symlink(string name, string target) => new(name, TarEntryType.SymbolicLink, null, target);
    public static TestEntry Whiteout(string dir, string name) => File(dir.TrimEnd('/') + "/.wh." + name, "");
    public static TestEntry Opaque(string dir) => File(dir.TrimEnd('/') + "/" + LayerStack.OpaqueMarker, "");

    public ImageModel Read(Layout layout = Layout.Both) => ImageReader.Read(Write(layout));

    public IImageFiles Write(Layout layout = Layout.Both)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "blobs", "sha256"));
        var layerDigests = new List<string>();
        var diffIds = new List<string>();
        foreach (var (_, entries) in _steps.Where(s => s.Entries is not null))
        {
            var tar = BuildTar(entries!);
            diffIds.Add(Digest(tar));
            layerDigests.Add(WriteBlob(Gzip ? Compress(tar) : tar));
        }

        var runConfig = new Dictionary<string, object?> { ["Env"] = DefaultEnv, ["Cmd"] = DefaultCmd, ["WorkingDir"] = "/app" };
        foreach (var (key, value) in ConfigFields) runConfig[key] = value;
        var imageConfig = new Dictionary<string, object?>
        {
            ["architecture"] = "amd64",
            ["os"] = "linux",
            ["config"] = runConfig,
            ["rootfs"] = new { type = "layers", diff_ids = diffIds },
            // Docker writes "history": null for a plain FROM scratch.
            ["history"] = _steps.Count == 0 ? null : _steps.Select((s, i) =>
            {
                var step = new Dictionary<string, object> { ["created_by"] = s.CreatedBy };
                if (s.Entries is null) step["empty_layer"] = true;
                if (i < StepTimes.Length) step["created"] = StepTimes[i];
                return step;
            }),
        };
        foreach (var (key, value) in ImageFields) imageConfig[key] = value;
        var config = WriteBlob(Json(imageConfig));

        if (layout is Layout.Legacy or Layout.Both)
        {
            object? layers = layerDigests.Count == 0 ? null : layerDigests.Select(BlobPath).ToList();
            WriteFile("manifest.json", Json(new[] { new { Config = BlobPath(config), RepoTags = DefaultRepoTags, Layers = layers } }));
        }
        if (layout is not Layout.Legacy)
        {
            var manifest = WriteBlob(Json(new
            {
                schemaVersion = 2,
                mediaType = "application/vnd.oci.image.manifest.v1+json",
                config = new { digest = config },
                layers = layerDigests.Select(d => new { digest = d }),
            }));
            var imageRef = new { mediaType = "application/vnd.oci.image.manifest.v1+json", digest = manifest };
            if (layout == Layout.OciNestedWithAttestation)
            {
                var attestation = WriteBlob(Json(new { schemaVersion = 2, config = new { digest = config }, layers = Array.Empty<object>() }));
                var nested = WriteBlob(Json(new
                {
                    manifests = new object[]
                    {
                        // Attestation first, to prove it is skipped rather than just not reached.
                        new { mediaType = "application/vnd.oci.image.manifest.v1+json", digest = attestation,
                              annotations = new Dictionary<string, string> { ["vnd.docker.reference.type"] = "attestation-manifest" } },
                        imageRef,
                    },
                }));
                WriteFile("index.json", Json(new { manifests = new[] { new { mediaType = "application/vnd.oci.image.index.v1+json", digest = nested } } }));
            }
            else
            {
                WriteFile("index.json", Json(new { manifests = new[] { imageRef } }));
            }
        }
        return new DirectoryImageFiles(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static byte[] BuildTar(TestEntry[] entries)
    {
        using var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var e in entries)
            {
                var entry = new PaxTarEntry(e.Type, e.Name) { Mode = e.Mode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite) };
                if (e.LinkTarget is not null) entry.LinkName = e.LinkTarget;
                if (e.Content is not null) entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(e.Content));
                writer.WriteEntry(entry);
            }
        }
        return ms.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(data);
        return ms.ToArray();
    }

    private string WriteBlob(byte[] data)
    {
        var digest = Digest(data);
        WriteFile(BlobPath(digest), data);
        return digest;
    }

    private void WriteFile(string path, byte[] data) => System.IO.File.WriteAllBytes(Path.Combine(_dir, path), data);
    private static string BlobPath(string digest) => "blobs/sha256/" + digest["sha256:".Length..];
    private static string Digest(byte[] data) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data));
    private static byte[] Json(object o) => JsonSerializer.SerializeToUtf8Bytes(o);
}

public sealed record TestEntry(string Name, TarEntryType Type, string? Content, string? LinkTarget = null, UnixFileMode? Mode = null);
