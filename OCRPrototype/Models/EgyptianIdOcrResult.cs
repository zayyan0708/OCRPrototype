using System.Text.Json.Serialization;

namespace OCRPrototype.Models;

/// <summary>
/// Shape of the JSON we hand back to the caller. Field name matches what was
/// asked for ("id informations", space and all) via JsonPropertyName rather
/// than trying to make that a valid C# identifier.
/// </summary>
public class EgyptianIdOcrResult
{
    [JsonPropertyName("id informations")]
    public List<string> IdInformations { get; set; } = new();
}
