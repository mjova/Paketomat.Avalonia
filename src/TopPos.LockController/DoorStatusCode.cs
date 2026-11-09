using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopPos.LockController
{
    public readonly struct DoorStatusCode
    {
        private readonly byte _value;

        public DoorStatusCode(byte value)
        {
            _value = value;
        }

        public bool IsOpen => _value != 0x00;
        public bool IsClosed => _value == 0x00;

        public byte RawValue => _value;

        public static DoorStatusCode FromByte(byte value) => new(value);

        public override string ToString()
            => IsOpen ? $"Open (0x{_value:X2})" : "Closed";

        public static implicit operator DoorStatusCode(byte value) => new(value);
        public static implicit operator byte(DoorStatusCode state) => state._value;

        public static readonly DoorStatusCode Open = new(0xFF);
        public static readonly DoorStatusCode Closed = new(0x00);
    }

    public class DoorStatusCodeJsonConverter : JsonConverter<DoorStatusCode>
    {
        public override DoorStatusCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Accept both numeric and string representations
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetByte(out byte value))
            {
                return new DoorStatusCode(value);
            }
            else if (reader.TokenType == JsonTokenType.String)
            {
                var str = reader.GetString();
                if (byte.TryParse(str, out value)) {
                    return new DoorStatusCode(value);
                }
                // Optionally, handle hex string (e.g., "0xFF")
                if (str != null && str.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                    byte.TryParse(str.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out value))
                {
                    return new DoorStatusCode(value);
                }
                throw new JsonException($"Invalid DoorStatusCode string value: {str}");
            }
            throw new JsonException($"Unexpected token parsing DoorStatusCode. Token: {reader.TokenType}");
        }

        public override void Write(Utf8JsonWriter writer, DoorStatusCode value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value.RawValue);
    }
}

