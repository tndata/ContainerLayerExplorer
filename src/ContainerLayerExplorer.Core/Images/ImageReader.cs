using System.Text.Json;

namespace ContainerLayerExplorer.Core.Images;

public sealed record ImageReadOptions(int MaxEntries = 200_000);

/// <summary>
/// Reads an unpacked <c>docker save</c> archive into an <see cref="ImageModel"/>.
/// Docker 25+ (containerd store) writes both an OCI layout (index.json) and a legacy manifest.json;
/// older Docker writes only manifest.json. manifest.json is preferred because it lists exactly
/// one image with its layers in order, without attestation manifests.
/// Whatever is wrong with the archive, the caller gets an <see cref="ImageReadException"/> with a reason.
/// </summary>
public static class ImageReader
{
    private const int MaxIndexDepth = 5;
    private const string ManifestFile = "manifest.json";

    public static ImageModel Read(IImageFiles files, ImageReadOptions? options = null)
    {
        try
        {
            return ReadImage(files, options ?? new ImageReadOptions());
        }
        catch (ImageReadException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
                                       or InvalidDataException or EndOfStreamException or FormatException)
        {
            // A shape of JSON or tar that nobody expected. Still the image's fault, not a crash.
            throw new ImageReadException($"it has unexpected content ({ex.Message})", ex);
        }
    }

    private static ImageModel ReadImage(IImageFiles files, ImageReadOptions options)
    {
        var (configPath, layerPaths) = files.Exists(ManifestFile) ? FromLegacyManifest(files) : FromOciIndex(files);

        using var configDoc = ParseJson(files, configPath, "the image config");
        var config = configDoc.RootElement;
        var diffIds = GetStrings(config, "rootfs", "diff_ids");
        var history = ReadHistory(config, layerPaths.Count);

        var stack = new LayerStack();
        var (layers, truncated) = ReadLayers(files, layerPaths, diffIds, history, stack, options.MaxEntries);

        var visible = stack.Visible.OrderBy(v => v.Path, StringComparer.Ordinal).ToList();
        return new ImageModel(
            GetString(config, "os") ?? "",
            GetString(config, "architecture") ?? "",
            ReadConfigSummary(config),
            history,
            layers,
            visible,
            layers.Sum(l => l.Size),
            visible.Where(v => v.Entry == EntryKind.File).Sum(v => v.Size),
            truncated);
    }

    /// <summary>
    /// Reads every layer in order and stacks them. Once <paramref name="maxEntries"/> entries have been read in all,
    /// the rest of the image is not read and the result is marked as cut off.
    /// </summary>
    private static (List<Layer> Layers, bool Truncated) ReadLayers(
        IImageFiles files, List<string> layerPaths, List<string> diffIds, List<HistoryStep> history, LayerStack stack, int maxEntries)
    {
        var layers = new List<Layer>();
        var entriesLeft = maxEntries;
        var truncated = false;
        for (var i = 0; i < layerPaths.Count; i++)
        {
            var entries = truncated ? [] : ReadLayerEntries(files, layerPaths[i], i, entriesLeft, out truncated);
            entriesLeft -= entries.Count;
            var changes = stack.Apply(i, entries);
            var size = entries.Where(e => e.Entry == EntryKind.File).Sum(e => e.Size);
            var createdBy = history.FirstOrDefault(h => h.LayerIndex == i)?.CreatedBy;
            layers.Add(new Layer(i, i < diffIds.Count ? diffIds[i] : "", layerPaths[i],
                files.Length(layerPaths[i]), size, createdBy, changes));
        }
        return (layers, truncated);
    }

    /// <summary>The entries of one layer, up to <paramref name="budget"/>; <paramref name="hitLimit"/> says there were more.</summary>
    private static List<RawEntry> ReadLayerEntries(IImageFiles files, string path, int index, int budget, out bool hitLimit)
    {
        var entries = new List<RawEntry>();
        hitLimit = false;
        try
        {
            using var stream = files.OpenRequired(path);
            foreach (var e in LayerReader.ReadEntries(stream))
            {
                if (entries.Count >= budget) { hitLimit = true; break; }
                entries.Add(e);
            }
        }
        catch (ImageReadException ex)
        {
            throw new ImageReadException($"layer {index + 1}: {ex.Reason}", ex.InnerException);
        }
        return entries;
    }

    private static (string Config, List<string> Layers) FromLegacyManifest(IImageFiles files)
    {
        using var doc = ParseJson(files, ManifestFile, ManifestFile);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            throw new ImageReadException("manifest.json is empty.");
        var first = doc.RootElement[0];
        if (first.ValueKind != JsonValueKind.Object) throw new ImageReadException("manifest.json is empty.");
        var layers = first.TryGetProperty("Layers", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => StringOrThrow(x, "manifest.json has an invalid layer entry.")).ToList()
            : []; // FROM scratch with no files: "Layers": null
        return (RequireString(first, "Config", ManifestFile), layers);
    }

    private static (string Config, List<string> Layers) FromOciIndex(IImageFiles files)
    {
        if (!files.Exists("index.json"))
            throw new ImageReadException("it is not a docker save archive (no manifest.json or index.json).");
        using var manifest = ParseJson(files, FindImageManifest(files, "index.json", depth: 0), "the image manifest");
        var root = manifest.RootElement;
        var layers = root.TryGetProperty("layers", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => BlobPath(RequireString(x, "digest", "a layer entry in the image manifest"))).ToList()
            : [];
        var configEntry = Require(root, "config", "the image manifest");
        return (BlobPath(RequireString(configEntry, "digest", "the config entry in the image manifest")), layers);
    }

    /// <summary>Walks (possibly nested) indexes to the first real image manifest, skipping attestations.</summary>
    private static string FindImageManifest(IImageFiles files, string indexPath, int depth)
    {
        if (depth > MaxIndexDepth) throw new ImageReadException("its image index is nested too deeply.");
        using var doc = ParseJson(files, indexPath, "the image index");
        var manifests = Require(doc.RootElement, "manifests", "the image index");
        if (manifests.ValueKind != JsonValueKind.Array) throw new ImageReadException("the image index has an invalid \"manifests\" entry.");
        foreach (var m in manifests.EnumerateArray())
        {
            if (IsAttestation(m)) continue;
            var path = BlobPath(RequireString(m, "digest", "an entry in the image index"));
            var mediaType = GetString(m, "mediaType") ?? "";
            if (mediaType.Contains("index", StringComparison.Ordinal) || mediaType.Contains("manifest.list", StringComparison.Ordinal))
                return FindImageManifest(files, path, depth + 1);
            return path;
        }
        throw new ImageReadException($"no image manifest was found in {indexPath}.");
    }

    private static bool IsAttestation(JsonElement m) =>
        m.ValueKind == JsonValueKind.Object
        && m.TryGetProperty("annotations", out var a)
        && a.ValueKind == JsonValueKind.Object
        && a.TryGetProperty("vnd.docker.reference.type", out var t)
        && t.GetString() == "attestation-manifest";

    /// <summary>Maps history steps to layers: steps marked empty_layer (ENV, CMD, LABEL...) have none.</summary>
    private static List<HistoryStep> ReadHistory(JsonElement config, int layerCount)
    {
        var steps = new List<HistoryStep>();
        // Plain "FROM scratch" gives "history": null.
        if (!config.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Array) return steps;
        var next = 0;
        foreach (var h in history.EnumerateArray())
        {
            var empty = h.TryGetProperty("empty_layer", out var e) && e.ValueKind == JsonValueKind.True;
            int? layer = !empty && next < layerCount ? next++ : null;
            DateTimeOffset? created = h.TryGetProperty("created", out var c) && c.TryGetDateTimeOffset(out var d) ? d : null;
            var text = GetString(h, "created_by") ?? GetString(h, "comment") ?? "";
            steps.Add(new HistoryStep(text, created, empty, layer));
        }
        return steps;
    }

    private static ImageConfigSummary ReadConfigSummary(JsonElement config)
    {
        if (!config.TryGetProperty("config", out var c) || c.ValueKind != JsonValueKind.Object)
            return new ImageConfigSummary([], [], [], null, null, []);
        var ports = c.TryGetProperty("ExposedPorts", out var p) && p.ValueKind == JsonValueKind.Object
            ? p.EnumerateObject().Select(x => x.Name).ToList()
            : [];
        return new ImageConfigSummary(
            GetStrings(c, "Env"), GetStrings(c, "Cmd"), GetStrings(c, "Entrypoint"),
            NullIfEmpty(GetString(c, "WorkingDir")), NullIfEmpty(GetString(c, "User")), ports);
    }

    private static string BlobPath(string digest)
    {
        var parts = digest.Split(':', 2);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0
            || parts[1].Contains('/') || parts[1].Contains('\\') || parts[1].Contains(".."))
            throw new ImageReadException($"it has a bad digest (\"{digest}\").");
        return $"blobs/{parts[0]}/{parts[1]}";
    }

    private static JsonDocument ParseJson(IImageFiles files, string path, string what)
    {
        try
        {
            return JsonDocument.Parse(ReadAll(files, path));
        }
        catch (JsonException ex)
        {
            throw new ImageReadException($"{what} ({path}) is not valid JSON.", ex);
        }
    }

    private static JsonElement Require(JsonElement parent, string name, string where)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            throw new ImageReadException($"{where} has no \"{name}\" entry.");
        return value;
    }

    private static string RequireString(JsonElement parent, string name, string where) =>
        StringOrThrow(Require(parent, name, where), $"{where} has an invalid \"{name}\" entry.");

    private static string StringOrThrow(JsonElement e, string message) =>
        e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s ? s : throw new ImageReadException(message);

    private static byte[] ReadAll(IImageFiles files, string path)
    {
        using var s = files.OpenRequired(path);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static List<string> GetStrings(JsonElement e, params string[] path)
    {
        foreach (var name in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e)) return [];
        }
        return e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
