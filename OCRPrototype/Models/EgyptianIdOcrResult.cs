using System.Text.Json.Serialization;

namespace OCRPrototype.Models;

public sealed class EgyptianIdOcrResult
{
    // Named properties are used internally by the application.
    // They are ignored by JSON so that the existing API contract
    // "id informations": [...] remains unchanged.

    [JsonIgnore]
    public string? FirstName { get; init; }

    [JsonIgnore]
    public string? RestOfName { get; init; }

    [JsonIgnore]
    public string? AddressLine1 { get; init; }

    [JsonIgnore]
    public string? AddressLine2 { get; init; }

    [JsonIgnore]
    public string? NationalId { get; init; }

    [JsonIgnore]
    public string? CardNumber { get; init; }

    /*
     * The position of every field is now fixed:
     *
     * [0] First name
     * [1] Rest of name
     * [2] Address line 1
     * [3] Address line 2
     * [4] National ID
     * [5] Card / serial number
     *
     * A missing value remains null instead of shifting later fields.
     */
    [JsonPropertyName("id informations")]
    public IReadOnlyList<string?> IdInformations =>
        new string?[]
        {
            FirstName,
            RestOfName,
            AddressLine1,
            AddressLine2,
            NationalId,
            CardNumber
        };

    [JsonIgnore]
    public bool HasAnyValue =>
        IdInformations.Any(value => !string.IsNullOrWhiteSpace(value));
}