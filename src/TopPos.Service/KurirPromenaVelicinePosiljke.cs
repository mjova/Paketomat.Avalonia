using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopPos.Service
{
	public enum Velicine { S, M, L }

	public class KurirPromenaVelicinePosiljke
	{
		public string SifraKurira { get; set; } = default!;

		public string SifraBenzinskeStanice { get; set; } = default!;

		public string Barkod { get; set; } = default!;
	}

	public class KurirPromenaVelicinePosiljkeResponse : ResponseBase
	{
		[JsonConverter(typeof(PackageSizeToEnumConverter))]
		public Velicine NovaVelicina { get; set; }
	}

	public class PackageSizeToEnumConverter : JsonConverter<Velicine>
	{
		public override Velicine Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			var value = reader.GetString();
			return value switch
						{
							"S" => Velicine.S,
							"M" => Velicine.M,
							"L" => Velicine.L,
							_ => throw new JsonException($"Unknown value '{value}' for enum {nameof(Velicine)}")
						};
		}

		public override void Write(Utf8JsonWriter writer, Velicine value, JsonSerializerOptions options)
			=> writer.WriteStringValue(value.ToString());
	}
}
