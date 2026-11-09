using System.Text.Json.Serialization;

namespace TopPos.PenPosService
{
    public class ErrorDetails
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("modelState")]
        public List<ModelStateEntry>? ModelState { get; set; }
    }

    public class ModelStateEntry
    {
        [JsonPropertyName("property")]
        public string? Property { get; set; }

        [JsonPropertyName("errors")]
        public List<string>? Errors { get; set; }
    }
}
