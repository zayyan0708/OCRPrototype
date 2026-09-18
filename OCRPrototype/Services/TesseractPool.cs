using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace OCRPrototype.Services;

public sealed class TesseractPool(IOptions<OcrOptions> configured, IWebHostEnvironment environment) : IHostedService, IDisposable
{
    private readonly OcrOptions _options = configured.Value;
    private readonly List<TesseractWorker> _workers = [];
    private readonly Channel<TesseractWorker> _available = Channel.CreateBounded<TesseractWorker>(configured.Value.Workers);
    private readonly SemaphoreSlim _admission = new(configured.Value.Workers + configured.Value.QueueCapacity);
    public bool TryAdmit() => _admission.Wait(0);
    public void ReleaseAdmission() => _admission.Release();
    public ValueTask<TesseractWorker> RentAsync(CancellationToken ct) => _available.Reader.ReadAsync(ct);
    public void Return(TesseractWorker worker) => _available.Writer.TryWrite(worker);

    public Task StartAsync(CancellationToken ct)
    {
        string data = Path.GetFullPath(_options.TessdataPath, environment.ContentRootPath);
        string face = Path.GetFullPath(_options.FaceModelPath, environment.ContentRootPath);
        foreach (string language in new[] { "ara", "eng" })
            if (!File.Exists(Path.Combine(data, language + ".traineddata")))
                throw new InvalidOperationException($"Missing {language}.traineddata. Run scripts/setup-models.ps1 first.");
        if (!File.Exists(face)) throw new InvalidOperationException("Missing face model. Run scripts/setup-models.ps1 first.");
        try
        {
            OpenCvSharp.Cv2.SetNumThreads(1);
            for (int i = 0; i < _options.Workers; i++)
            {
                ct.ThrowIfCancellationRequested();
                var worker = new TesseractWorker(data, face);
                _workers.Add(worker); _available.Writer.TryWrite(worker);
            }
        }
        catch { foreach (var worker in _workers) worker.Dispose(); _workers.Clear(); throw; }
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public void Dispose()
    {
        foreach (var worker in _workers) worker.Dispose();
        _admission.Dispose();
    }
}
