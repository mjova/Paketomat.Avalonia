using System.Text.Json.Serialization;

namespace TopPos.PenPosService
{
    public class ProcessorStatus
    {
        [JsonPropertyName("isPinRequired")]
        public bool IsPinRequired { get; set; }

        [JsonPropertyName("auditRequired")]
        public bool AuditRequired { get; set; }

        [JsonPropertyName("lastInvoiceNumber")]
        public string? LastInvoiceNumber { get; set; }

        [JsonPropertyName("protocolVersion")]
        public string? ProtocolVersion { get; set; }

        [JsonPropertyName("secureElementVersion")]
        public string? SecureElementVersion { get; set; }

        [JsonPropertyName("hardwareVersion")]
        public string? HardwareVersion { get; set; }

        [JsonPropertyName("softwareVersion")]
        public string? SoftwareVersion { get; set; }

        [JsonPropertyName("deviceSerialNumber")]
        public string? DeviceSerialNumber { get; set; }

        [JsonPropertyName("make")]
        public string? Make { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("mssc")]
        public List<string>? Mssc { get; set; }

        [JsonPropertyName("gsc")]
        public List<ProcessorResultCode>? Gsc { get; set; }

        [JsonPropertyName("sdcDateTime")]
        public DateTimeOffset SdcDateTime { get; set; }

        [JsonPropertyName("supportedLanguages")]
        public List<string>? SupportedLanguages { get; set; }

        [JsonPropertyName("uid")]
        public string? Uid { get; set; }

        [JsonPropertyName("taxCoreApi")]
        public string? TaxCoreApi { get; set; }

        [JsonPropertyName("currentTaxRates")]
        public object? CurrentTaxRates { get; set; }

        [JsonPropertyName("allTaxRates")]
        public object? AllTaxRates { get; set; }

        //public bool IsSuccess => Gsc != null && Gsc.All(c => )
    }
}
