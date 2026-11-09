using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopPos.PenPosService
{
    public class ProcessorResultCode
    {
        public enum CodeSeverity { Info, Warning, Error }

        public string Code { get; private set; }
        public ProcessorResultCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) { 
                throw new ArgumentException("Code cannot be null or empty.", nameof(code));
            }
            if (code.Length < 4) {
                throw new ArgumentException("Code length must be at least 4 characters.", nameof(code));
            }
            Code = code;
        }

        public CodeSeverity Severity => Code[0] switch
                            {
                                '0' => CodeSeverity.Info,
                                '1' => CodeSeverity.Warning,
                                '2' => CodeSeverity.Error,
                                _ => throw new InvalidOperationException($"Unknown severity for code: {Code}")
                            };

        public static bool operator ==(ProcessorResultCode? left, ProcessorResultCode? right)
        {
            if (ReferenceEquals(left, right)) {
                return true;
            }
            if (left is null || right is null) {
                return false;
            }
            return left.Code == right.Code;
        }

        public static bool operator !=(ProcessorResultCode? left, ProcessorResultCode? right) => !(left == right);
       
        public override bool Equals(object? obj) => this == obj as ProcessorResultCode;
        
        public override int GetHashCode() => this.Code.GetHashCode();

        // Info
        public static readonly ProcessorResultCode OK = new("0000"); // Command is executed without warnings or errors
        public static readonly ProcessorResultCode PinOK = new("0100"); // PIN code is correct
        public static readonly ProcessorResultCode InternetConnectionAvailable = new("0210"); // Internet Connection is available (optional)
        public static readonly ProcessorResultCode InternetConnectionNotAvailable = new("0220"); // Internet Connection is not available (optional)
        // Warning
        public static readonly ProcessorResultCode Storage90Full = new("1100"); // Storage used to store audit packages is 90% full.It is time to perform the audit.
        public static readonly ProcessorResultCode SmartCardNotPresent = new("1300"); // Secure element card is not inserted in the E-SDC smart card reader
        public static readonly ProcessorResultCode AuditRequired = new ("1400"); // Total Sale and Refund amount reached 75% of the SE limit.It is time to perform the audit.
        public static readonly ProcessorResultCode PinCodeRequired = new("1500"); // Indicates that POS must provide the PIN code
        public static readonly ProcessorResultCode UndefinedWarning = new("1900"); // Something is wrong but specific warning is not defined for that situation.Manufacturer can use manufacturer-specific codes to describe warning in more details
        // Error
        public static readonly ProcessorResultCode PinNotOK = new("2100"); // PIN code sent by the POS is invalid
        public static readonly ProcessorResultCode CardLocked = new("2110"); // The number of allowed PIN entries exceeded. The card is locked for use
        public static readonly ProcessorResultCode SecureElementLocked = new("2210"); // Secure Element is locked. No additional invoices can be signed before the audit is completed
        public static readonly ProcessorResultCode SecureElementCommunicationFailed = new("2220"); // E-SDC cannot connect to the Secure Element applet
        public static readonly ProcessorResultCode SecureElementProtocolMismatch = new("2230"); // Secure Element does not support requested protocol version (reserved for later use)
        public static readonly ProcessorResultCode InvalidTaxLabels = new("2310"); // Tax Labels sent by the POS are not defined
        public static readonly ProcessorResultCode NotConfigured = new("2400"); // SDC device is not fully configured for invoice signing(i.e.tax rates or verification URL are missing etc.)
        public static readonly ProcessorResultCode FieldRequired = new("2800"); // A field is required (a mandatory invoice request field is missing)
        public static readonly ProcessorResultCode FieldValueTooLong = new("2801"); // The length of the field value is longer than expected
        public static readonly ProcessorResultCode FieldValueTooShort = new("2802"); // The length of the field value is shorter than expected
        public static readonly ProcessorResultCode InvalidFieldLength = new("2803"); // The length of the field value is shorter or longer than expected
        public static readonly ProcessorResultCode FieldOutOfRange = new("2804"); // A field value out of expected range
        public static readonly ProcessorResultCode InvalidFieldValue = new("2805"); // A field contains an invalid value
        public static readonly ProcessorResultCode InvalidFieldDataFormat = new("2806"); // The data format is invalid
        public static readonly ProcessorResultCode ListTooShort = new("2807"); // The list of items or the list of tax labels in the invoice request does not contain at least one element(item/label).
        public static readonly ProcessorResultCode ListTooLong = new("2808"); // The list of items or the list of tax labels in the invoice request exceeds the maximum allowed number of elements (items/labels) or byte size.The maximum values depend on an SDC's capacity for processing invoice requests and can be manufacturer-specific.
    }



    //  public static readonly ProcessorResult[] KnownResults = [OK, NoInternetConnection, NoSecurityCertificate, PinNotEntered, PinLocked, NotInitialized];

    public class ProcessorResultCodeJsonConverter : JsonConverter<ProcessorResultCode>
    {
        public override ProcessorResultCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var str = reader.GetString() ?? string.Empty;
                return new ProcessorResultCode(str);
            }
            throw new JsonException($"Unexpected token parsing {nameof(ProcessorResultCode)}. Token: {reader.TokenType}");
        }

        public override void Write(Utf8JsonWriter writer, ProcessorResultCode value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Code);
    }

}
