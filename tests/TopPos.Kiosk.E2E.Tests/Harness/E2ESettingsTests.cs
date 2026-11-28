using TopPos.E2E.Tests.Infrastructure;
using Xunit;

namespace TopPos.E2E.Tests.Harness
{
	/// <summary>Samoprovera harness-a: veličina prozora kiosk-a iz TOPPOS_E2E_SCREEN.</summary>
	public sealed class E2ESettingsTests
	{
		[Theory]
		[InlineData("1280x800", 1280, 800)]
		[InlineData(" 600 x 1024 ", 600, 1024)]
		[InlineData("1280*800", 1280, 800)]
		[InlineData("600X1024", 600, 1024)]
		public void ParseScreen_WidthByHeight_ReturnsSize(string value, int width, int height)
			=> Assert.Equal((width, height), E2ESettings.ParseScreen(value));

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		public void ParseScreen_NotSet_TemplateSizeIsUsed(string? value)
			=> Assert.Null(E2ESettings.ParseScreen(value));

		[Theory]
		[InlineData("1280:800")]
		[InlineData("1280x")]
		[InlineData("0x800")]
		[InlineData("1280x800x2")]
		public void ParseScreen_Malformed_Throws(string value)
			=> Assert.Contains(E2ESettings.ScreenVariable, Assert.Throws<InvalidOperationException>(() => E2ESettings.ParseScreen(value)).Message);
	}
}