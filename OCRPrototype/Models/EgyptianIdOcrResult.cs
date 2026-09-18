using System.Text.Json.Serialization;

namespace OCRPrototype.Models;

public class EgyptianIdOcrResult
{
    [JsonPropertyName("isSuccess")]
    public bool IsSuccess { get; set; }

    [JsonPropertyName("quality")]
    public ImageQualityInfo Quality { get; set; } = new();

    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("nationalId")]
    public string? NationalId { get; set; }

    [JsonPropertyName("cardId")]
    public string? CardId { get; set; }

    [JsonPropertyName("dateOfBirth")]
    public string? DateOfBirth { get; set; }

    [JsonPropertyName("photo")]
    public string? Photo { get; set; }

    [JsonPropertyName("warning")]
    public string? Warning { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

public class ImageQualityInfo
{
    [JsonPropertyName("isReadable")]
    public bool IsReadable { get; set; }

    [JsonPropertyName("sharpness")]
    public double Sharpness { get; set; }

    [JsonPropertyName("brightness")]
    public double Brightness { get; set; }

    [JsonPropertyName("contrast")]
    public double Contrast { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}