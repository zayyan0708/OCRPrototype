using OCRPrototype.Models;

namespace OCRPrototype.Services;

public interface IEgyptianIdOcrService
{
    Task<EgyptianIdOcrResult> ExtractAsync(
        Stream imageStream,
        CancellationToken ct = default);
}