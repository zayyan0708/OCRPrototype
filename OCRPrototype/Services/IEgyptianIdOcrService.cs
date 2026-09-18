using OCRPrototype.Models;

namespace OCRPrototype.Services;

public interface IEgyptianIdOcrService
{
    Task<PipelineResult> ExtractAsync(Stream imageStream, CancellationToken ct = default);
}
