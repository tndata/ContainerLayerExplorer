using ContainerLayerExplorer.Core.Images;
using ContainerLayerExplorer.Docker;
using Microsoft.Extensions.Options;

namespace ContainerLayerExplorer.Images;

public sealed record LoadedImage(string Id, string Name, ImageModel Model, IImageFiles Files);

/// <summary>
/// Saves images from Docker into temp folders and keeps the parsed models of the last few.
/// Keyed by image ID: an ID always means the same content, so a cached entry is never stale.
/// </summary>
public sealed class ImageCache : IDisposable
{
    private const int MaxImages = 6;

    private readonly DockerClient _docker;
    private readonly ILogger<ImageCache> _logger;
    private readonly CacheFolder _folder;
    private readonly string _root; // this copy's own folder
    private readonly List<LoadedImage> _images = []; // most recently used last
    private readonly SemaphoreSlim _lock = new(1, 1);

    private readonly Limits _limits;

    public ImageCache(DockerClient docker, IOptions<Limits> limits, ILogger<ImageCache> logger)
    {
        _docker = docker;
        _limits = limits.Value;
        _logger = logger;
        _folder = CacheFolder.Create(CacheFolder.DefaultBase, logger); // also removes old folders left by earlier runs
        _root = _folder.Path;
    }

    public async Task<LoadedImage?> LoadAsync(string name, CancellationToken ct)
    {
        var info = await _docker.GetImageInfoAsync(name, ct);
        if (info is null) return null;
        var id = info.Id;

        await _lock.WaitAsync(ct);
        try
        {
            var cached = _images.FirstOrDefault(i => i.Id == id);
            if (cached is not null)
            {
                _images.Remove(cached);
                _images.Add(cached with { Name = name });
                return _images[^1];
            }

            ImageGuard.CheckSize(info.Size, _limits.MaxImageSizeMb);
            ImageGuard.CheckFreeSpace(info.Size, ImageGuard.FreeSpaceOf(_root), _root);

            var dir = Path.Combine(_root, id.Replace("sha256:", "")[..16]);
            TryDelete(dir);
            Directory.CreateDirectory(dir);
            var files = new DirectoryImageFiles(dir);
            ImageModel model;
            if (info.LayerCount == 0)
            {
                // e.g. FROM scratch with nothing added yet. Docker refuses to save it ("empty export"), so skip the save.
                model = new ImageModel(info.Os, info.Architecture, new ImageConfigSummary([], [], [], null, null, []),
                    [], [], [], 0, 0, false);
            }
            else
            {
                await using (var tar = await _docker.SaveImageAsync(id, ct))
                {
                    await ImageExtractor.ExtractAsync(tar, dir, ct);
                }
                model = ImageReader.Read(files, new ImageReadOptions(_limits.MaxEntries));
            }
            var loaded = new LoadedImage(id, name, model, files);
            _images.Add(loaded);
            Evict();
            _logger.LogInformation("Loaded {Name} ({Id}): {Layers} layers", name, id[..19], loaded.Model.Layers.Count);
            return loaded;
        }
        finally
        {
            _lock.Release();
        }
    }

    public LoadedImage? Get(string id)
    {
        _lock.Wait();
        try { return _images.FirstOrDefault(i => i.Id == id); }
        finally { _lock.Release(); }
    }

    private void Evict()
    {
        while (_images.Count > MaxImages)
        {
            var old = _images[0];
            _images.RemoveAt(0);
            TryDelete(Path.Combine(_root, old.Id.Replace("sha256:", "")[..16]));
        }
    }

    private void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete {Dir}: {Message}", dir, ex.Message);
        }
    }

    public void Dispose() => _folder.Dispose();
}
