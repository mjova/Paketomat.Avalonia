using System.Text.Json.Serialization;

namespace TopPos.PenPosService
{
    public enum InvoiceType { Normal, ProForma, Copy, Training }

    public enum TransactionType { Sale, Refund }

    public enum PaymentType { Other, Cash, Card, Check, WireTransfer, Voucher, MobileMoney }

    public class PaymentItem
    {
        [JsonPropertyName("amount")]
        public decimal Amount { get; set; } //required

        [JsonPropertyName("paymentType")]
        [JsonConverter(typeof(JsonStringEnumConverter<PaymentType>))]
        public PaymentType PaymentType { get; set; } //required
    }

    public class InvoiceItem
    {
        [JsonPropertyName("gtin")]
        public string Gtin { get; set; } = default!;    //required

        [JsonPropertyName("quantity")]
        public double Quantity { get; set; }    //required

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("unitPrice")]
        public decimal? UnitPrice { get; set; }

        [JsonPropertyName("labels")]
        public List<string>? Labels { get; set; }
    }

    public class InvoiceRequest
    {
        [JsonPropertyName("invoiceType")]
        [JsonConverter(typeof(JsonStringEnumConverter<InvoiceType>))]
        public InvoiceType InvoiceType { get; set; } = default!;     //required

        [JsonPropertyName("transactionType")]
        [JsonConverter(typeof(JsonStringEnumConverter<TransactionType>))]
        public TransactionType TransactionType { get; set; } = default!; //required

        [JsonPropertyName("cashier")]
        public string? Cashier { get; set; } = default!;  //required

        [JsonPropertyName("items")]
        public List<InvoiceItem> Items { get; set; } = default!;   //required

        [JsonPropertyName("payment")]
        public List<PaymentItem> Payment { get; set; } = default!;  //required

        [JsonPropertyName("buyerId")]
        public string? BuyerId { get; set; }

        [JsonPropertyName("buyerCostCenterId")]
        public string? BuyerCostCenterId { get; set; }

        [JsonPropertyName("referentDocumentNumber")]
        public string? ReferentDocumentNumber { get; set; }

        [JsonPropertyName("referentDocumentDT")]
        public DateTime? ReferentDocumentDateTime { get; set; }

        [JsonPropertyName("advertisement")]
        public string? Advertisement { get; set; }
    }
}
