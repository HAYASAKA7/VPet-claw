using System.Text.Json.Serialization;

namespace VPet.Plugin.OpenClawBridge.Models
{
    public class OpenClawMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("payload")]
        public string? Payload { get; set; }

        [JsonPropertyName("isFinal")]
        public bool IsFinal { get; set; }

        [JsonPropertyName("isDelta")]
        public bool IsDelta { get; set; }

        [JsonPropertyName("replace")]
        public bool Replace { get; set; }

        [JsonPropertyName("runId")]
        public string? RunId { get; set; }

        [JsonPropertyName("tool")]
        public string? Tool { get; set; }

        [JsonPropertyName("args")]
        public string? Args { get; set; }
    }
}
