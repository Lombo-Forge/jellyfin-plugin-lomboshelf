using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.LomboShelf.Services;

/// <summary>Shape File Transformation hands to a callback: { "contents": "..." }.</summary>
public class PatchRequestPayload
{
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}
