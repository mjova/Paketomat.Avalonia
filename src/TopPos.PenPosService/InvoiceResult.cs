using System.Text.Json.Serialization;

namespace TopPos.PenPosService
{
    public class TaxItem
    {
        [JsonPropertyName("categoryType")]
        public int CategoryType { get; set; }

        [JsonPropertyName("label")]
        public string Label { get; set; } = default!;

        [JsonPropertyName("amount")]
        public double Amount { get; set; }

        [JsonPropertyName("rate")]
        public double? Rate { get; set; }

        [JsonPropertyName("categoryName")]
        public string CategoryName { get; set; } = default!;
    }

    public class InvoiceResult
    {
        [JsonPropertyName("sdcDateTime")]
        public DateTime? SdcDateTime { get; set; }

        [JsonPropertyName("invoiceNumber")]
        public string? InvoiceNumber { get; set; }

        [JsonPropertyName("taxItems")]
        public List<TaxItem>? TaxItems { get; set; }

        [JsonPropertyName("verificationUrl")]
        public string? VerificationUrl { get; set; }

        [JsonPropertyName("totalAmount")]
        public double? TotalAmount { get; set; }
    }
}
