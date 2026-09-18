using System.Text.Json.Serialization;

namespace OCRPrototype.Models;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(OcrExtractionResult))]
public partial class OcrJsonContext : JsonSerializerContext { }
