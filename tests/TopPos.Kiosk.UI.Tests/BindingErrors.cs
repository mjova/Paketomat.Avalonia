using Avalonia.Logging;

namespace TopPos.Kiosk.UI.Tests
{
	/// <summary>
	/// Hvata upozorenja i greške povezivanja (Avalonia <see cref="LogArea.Binding"/>, npr. putanja koja ne postoji u
	/// ViewModel-u ili pogrešan tip) dok traje; po završetku vraća prethodni <see cref="Logger.Sink"/>.
	/// </summary>
	internal sealed class BindingErrors : ILogSink, IDisposable
	{
		private readonly ILogSink? _previous = Logger.Sink;
		private readonly List<string> _messages = [];

		public BindingErrors() => Logger.Sink = this;

		public IReadOnlyList<string> Messages => _messages;

		public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;

		public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
			=> Log(level, area, source, messageTemplate, []);

		public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
		{
			if (IsEnabled(level, area)) {
				_messages.Add($"{messageTemplate} [{string.Join(", ", propertyValues)}]");
			}
		}

		public void Dispose() => Logger.Sink = _previous;
	}
}