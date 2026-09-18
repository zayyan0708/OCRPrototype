using Microsoft.Extensions.Options;

namespace OCRPrototype.Services;

public sealed class PortraitStore(IOptions<OcrOptions> options, IWebHostEnvironment environment)
{
    private readonly string _root = Path.GetFullPath(options.Value.ArtifactPath, environment.ContentRootPath);
    public async Task<string> SaveAsync(byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jpg");
        try { await File.WriteAllBytesAsync(path, bytes, ct); return path; }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    public void RemoveExpired()
    {
        if (!Directory.Exists(_root)) return;
        var before = DateTime.UtcNow.AddHours(-options.Value.ArtifactRetentionHours);
        foreach (string path in Directory.EnumerateFiles(_root, "*.jpg"))
            if (File.GetLastWriteTimeUtc(path) < before) File.Delete(path);
    }
}

public sealed class PortraitCleanup(PortraitStore store, ILogger<PortraitCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { store.RemoveExpired(); }
            catch (IOException) { logger.LogWarning("Portrait retention cleanup could not complete."); }
            catch (UnauthorizedAccessException) { logger.LogWarning("Portrait retention directory is not accessible."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
