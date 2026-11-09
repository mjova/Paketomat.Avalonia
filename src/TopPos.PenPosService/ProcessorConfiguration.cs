using System.Text.Json.Serialization;

namespace TopPos.PenPosService
{
    public class ProcessorConfiguration
    {
        [JsonPropertyName("organizationTaxNumber")]
        public string OrganizationTaxNumber { get; set; } = default!;

        [JsonPropertyName("organizationName")]
        public string OrganizationName { get; set; } = default!;

        [JsonPropertyName("organizationUnit")]
        public string OrganizationUnit { get; set; } = default!;

        [JsonPropertyName("secureElementNumber")]
        public string SecureElementNumber { get; set; } = default!;

        [JsonPropertyName("time")]
        public DateTimeOffset Time { get; set; }

        [JsonPropertyName("licenceNumber")]
        public string LicenceNumber { get; set; } = default!;

        [JsonPropertyName("vatPayer")]
        public bool VatPayer { get; set; }

        [JsonPropertyName("locationId")]
        public string LocationId { get; set; } = default!;

        [JsonPropertyName("environment")]
        public string Environment { get; set; } = default!;

        [JsonPropertyName("pinRequired")]
        public bool PinRequired { get; set; }

        [JsonPropertyName("pendingAuditPackages")]
        public int PendingAuditPackages { get; set; }

        [JsonPropertyName("lpfrVersion")]
        public string LpfrVersion { get; set; } = default!;
    }
}
