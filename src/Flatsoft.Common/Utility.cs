using System.Text;
using System.Text.Json;

namespace Flatsoft.Common
{
	public static class Utility
	{
		public static T? ReadConfiguration<T>(string filePath, out Exception? exception) where T : class 
		{
			try
			{
				exception = null;
				string json = File.ReadAllText(filePath);
				var config = JsonSerializer.Deserialize<T>(json);
				return config;
			}
			catch (Exception ex) {
				exception = ex;
				return null;
			}
		}

		public static string? GetExceptionMessages(this Exception? ex)
		{
			if (ex?.InnerException == null) {
				return ex?.Message;
			}
			StringBuilder sb = new StringBuilder();
			while (ex != null)
			{
				sb.AppendLine(ex.Message);
				ex = ex.InnerException;
			}
			return sb.ToString();
		}
	}
}
