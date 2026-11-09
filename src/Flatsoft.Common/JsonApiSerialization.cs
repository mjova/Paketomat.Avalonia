using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flatsoft.Common.Text
{
	public static class JsonApiSerialization
	{
		private static readonly JsonSerializerOptions Options;

		static JsonApiSerialization()
		{
			Options = new JsonSerializerOptions
			{
#if DEBUG
				WriteIndented = true,
#endif
				PropertyNameCaseInsensitive = true,
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase
			};
		}

		[return: MaybeNull]
		public static T Deserialize<T>(string json)
			=> JsonSerializer.Deserialize<T>(json, Options);

		public static string Serialize<T>(T obj, JsonSerializerOptions? options = null)
			=> JsonSerializer.Serialize(obj, options ?? Options);

		public static ValueTask<T?> DeserializeAsync<T>(Stream utf8Json, CancellationToken ct, JsonSerializerOptions? options = null)
			=> JsonSerializer.DeserializeAsync<T>(utf8Json, options ?? Options, ct);

		public static void RegisterJsonConverter(JsonConverter converter)
			=> Options.Converters.Add(converter);
	}

	public class TimeSpanJsonConverter : JsonConverter<TimeSpan?>
	{
		private static char[] __abbreviatedPeriods = { 'd', 'h', 'm', 's' };

		public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType == JsonTokenType.String)
			{
				var str = reader.GetString()?.Trim();
				if (!string.IsNullOrEmpty(str)) {
					// "{count} d|h|m|s
					int idx = str.IndexOfAny(__abbreviatedPeriods);
					if (idx != -1) {	
						if (int.TryParse(str.AsSpan(0, idx), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) {
							return str[idx] switch
                            {
                                'd' => TimeSpan.FromDays(count),
                                'h' => TimeSpan.FromHours(count),
                                'm' => TimeSpan.FromMinutes(count),
                                's' => TimeSpan.FromSeconds(count),
                                _ => throw new NotSupportedException($"'{str[idx]}'")
                            };
						}
					}
					else if (TimeSpan.TryParse(str, out var period)) {	// Fallback
						return period;
                    }
				}
				throw new JsonException($"Value '{str}' cannot be converted to a time period. Expected format: <count> d|h|m|s, or a valid time span value");
			}
			throw new JsonException($"Unsupported token type: {reader.TokenType}");
		}

		public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
			=> throw new NotImplementedException();

		public static readonly TimeSpanJsonConverter Default = new();
	}
	
	public class EnumConverter<T> : JsonConverter<T> where T : Enum
	{
		private static readonly T[] __allowedEnumValues = (T[]) Enum.GetValues(typeof(T));

		public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType == JsonTokenType.String) {
				var str = reader.GetString()?.Trim();
				if (Enum.TryParse(typeof(T), str, ignoreCase: true, out var value)) {
					if (value == null) {
						throw new JsonException($"Value '{str}' is not a valid {nameof(T)}. Valid values are {string.Join(',', __allowedEnumValues.Select(v => v.ToString()))}");
					}
					return (T) value;
				}
				throw new JsonException($"Value '{str}' is not a valid {nameof(T)}. Valid values are {string.Join(',', __allowedEnumValues.Select(v => v.ToString()))}");
			}
			else if (reader.TokenType == JsonTokenType.Number) {
				var value = reader.GetInt16();
				var idx = Array.IndexOf(__allowedEnumValues, value);
				if (idx < 0) {
					throw new JsonException($"Value '{value}' cannot be converted to an entity property type. Expected one of the following values: {string.Join(',', __allowedEnumValues)}");
				}
				return __allowedEnumValues[idx];
			}
			throw new JsonException($"Unsupported token type: {reader.TokenType}");
		}

		public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
			=> throw new NotSupportedException();
	}

}
