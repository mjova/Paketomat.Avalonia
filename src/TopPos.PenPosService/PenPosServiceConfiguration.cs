namespace TopPos.PenPosService
{
    public class PenPosServiceConfiguration
    {
        public bool IsEnabled { get; set; } = false;

        public string BaseManagementUrl { get; set; } = default!;

        public string BaseInvoiceUrl { get; set; } = default!;

        public string Pin { get; set; } = default!;

        public string? ItemName { get; set; }

        public string? TaxCategory { get; set; }

        public string Cashier { get; set; } = default!;

        public int? RequestTimeout { get; set; }
    }
}
