namespace OCRPrototype.Models;

public record OcrExtractionResult(
    bool IsSuccess,
    double QualityScore,
    ExtractedCardData? Data,
    ExtractedArtifacts? Artifacts,
    string? ErrorMessage);

public record ExtractedCardData(
    string? FullName,
    string? Address,
    string? NationalId,
    string? CardId,
    DateTime? DateOfBirth);

public record ExtractedArtifacts(string? PhotoPath);

public enum OcrFailure { None, InvalidImage, Unreadable, Incomplete, Busy, Unavailable }
public sealed record PipelineResult(OcrExtractionResult Result, OcrFailure Failure);
public sealed record ImageQuality(double BlurVariance, double Brightness, double Contrast,
    double EffectiveDpi, double? MetadataDpi, double Score, string? Rejection);
