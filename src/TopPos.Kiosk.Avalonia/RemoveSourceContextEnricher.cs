using Serilog.Core;
using Serilog.Events;

namespace TopPos.Kiosk.UI
{
	public class RemoveSourceContextEnricher : ILogEventEnricher
	{
		public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
		{
			logEvent.RemovePropertyIfPresent("SourceContext");
		}
	}
}