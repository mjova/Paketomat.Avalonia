namespace TopPos.Kiosk.UI.Controls
{
	/// <summary>Veličina ekrana po kraćoj strani prozora (v2 <c>LayoutState.ScreenSize</c>).</summary>
	public enum ScreenSize
	{
		Large,
		Medium,
		Small
	}

	/// <summary>
	/// Layout ekrana (v2 <c>LayoutState</c>): orijentacija i veličina. Bira jedan od šest prikaza stranice i glavnog prozora
	/// (<see cref="LayoutContent"/>).
	/// </summary>
	public readonly record struct ScreenLayout(bool IsPortrait, ScreenSize Size)
	{
		public static readonly ScreenLayout LandscapeLarge = new(false, ScreenSize.Large);

		/// <summary>
		/// v2 <c>MainWindow.OnRenderSizeChanged</c>: uspravan je prozor viši nego širi; veličinu određuje kraća strana
		/// (&lt; 700 Small, &gt; 790 Large, inače Medium), pa uzak uspravan ekran (600×1024) nije Large zbog visine.
		/// Rezolucije iz v2 konfiguracije: 1280×800 Landscape Large, 1024×768 Landscape Medium, 600×1024 Portrait Small.
		/// </summary>
		public static ScreenLayout FromSize(double width, double height)
		{
			var constrainingSide = Math.Min(width, height);
			var size = constrainingSide < 700 ? ScreenSize.Small
					 : constrainingSide > 790 ? ScreenSize.Large
					 : ScreenSize.Medium;
			return new ScreenLayout(height > width, size);
		}

		public override string ToString() => $"{(IsPortrait ? "Portrait" : "Landscape")}{Size}";
	}
}