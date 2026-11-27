using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace TopPos.Kiosk.UI.Controls
{
	/// <summary>
	/// Sadržaj u jednom od šest layouta (v2 <c>LayoutTemplateSelector</c> + <c>EnableResponsiveTemplates</c>): šablon se bira po
	/// <see cref="ScreenLayoutProperty"/>, koji glavni prozor postavlja po svojoj veličini i koji se nasleđuje niz stablo.
	/// Promena layouta (rotacija, promena veličine prozora) odmah menja prikaz; ViewModel ostaje isti, pa se stanje ne gubi
	/// (v2 je to radio samo za glavni prozor i skeniranje kurira, ostale stranice tek pri sledećem prikazu).
	/// Padanje kao v2 (Medium bez svog → Small → Large), uz razliku: bez šablona za tekući layout ne ostaje prazno, nego
	/// se uzima najbliži iste orijentacije, pa iz druge orijentacije (<see cref="Select{T}"/>). U v2 bi
	/// <c>null</c> iz selektora prikazao ime ViewModel-a; ovde bi bez šablona sadržaj ponovo dobio šablon stranice
	/// iz <c>App.axaml</c> i ugnezdio sam sebe.
	/// </summary>
	public class LayoutContent : ContentControl
	{
		public static readonly AttachedProperty<ScreenLayout> ScreenLayoutProperty =
			AvaloniaProperty.RegisterAttached<LayoutContent, Control, ScreenLayout>("ScreenLayout", ScreenLayout.LandscapeLarge, inherits: true);

		public static readonly StyledProperty<IDataTemplate?> LandscapeLargeTemplateProperty = AvaloniaProperty.Register<LayoutContent, IDataTemplate?>(nameof(LandscapeLargeTemplate));
		public static readonly StyledProperty<IDataTemplate?> LandscapeMediumTemplateProperty = AvaloniaProperty.Register<LayoutContent, IDataTemplate?>(nameof(LandscapeMediumTemplate));
		public static readonly StyledProperty<IDataTemplate?> LandscapeSmallTemplateProperty = AvaloniaProperty.Register<LayoutContent, IDataTemplate?>(nameof(LandscapeSmallTemplate));
		public static readonly StyledProperty<IDataTemplate?> PortraitLargeTemplateProperty = AvaloniaProperty.Register<LayoutContent, IDataTemplate?>(nameof(PortraitLargeTemplate));
		public static readonly StyledProperty<IDataTemplate?> PortraitMediumTemplateProperty = AvaloniaProperty.Register<LayoutContent, IDataTemplate?>(nameof(PortraitMediumTemplate));
		public static readonly StyledProperty<IDataTemplate?> PortraitSmallTemplateProperty = AvaloniaProperty.Register<LayoutContent, IDataTemplate?>(nameof(PortraitSmallTemplate));

		// Dok nijedan šablon nije postavljen (XAML ih postavlja jedan po jedan): prazno, nikad šablon iz App.axaml.
		private static readonly IDataTemplate __empty = new FuncDataTemplate<object?>((_, _) => new Panel());

		public static ScreenLayout GetScreenLayout(Control control) => control.GetValue(ScreenLayoutProperty);
		public static void SetScreenLayout(Control control, ScreenLayout value) => control.SetValue(ScreenLayoutProperty, value);

		public IDataTemplate? LandscapeLargeTemplate { get => GetValue(LandscapeLargeTemplateProperty); set => SetValue(LandscapeLargeTemplateProperty, value); }
		public IDataTemplate? LandscapeMediumTemplate { get => GetValue(LandscapeMediumTemplateProperty); set => SetValue(LandscapeMediumTemplateProperty, value); }
		public IDataTemplate? LandscapeSmallTemplate { get => GetValue(LandscapeSmallTemplateProperty); set => SetValue(LandscapeSmallTemplateProperty, value); }
		public IDataTemplate? PortraitLargeTemplate { get => GetValue(PortraitLargeTemplateProperty); set => SetValue(PortraitLargeTemplateProperty, value); }
		public IDataTemplate? PortraitMediumTemplate { get => GetValue(PortraitMediumTemplateProperty); set => SetValue(PortraitMediumTemplateProperty, value); }
		public IDataTemplate? PortraitSmallTemplate { get => GetValue(PortraitSmallTemplateProperty); set => SetValue(PortraitSmallTemplateProperty, value); }

		/// <summary>Izgled (template) običnog <see cref="ContentControl"/>-a iz teme.</summary>
		protected override Type StyleKeyOverride => typeof(ContentControl);

		public LayoutContent()
		{
			ContentTemplate = __empty;
		}

		/// <summary>
		/// Šablon za <paramref name="layout"/>; <c>null</c> samo kad nijedan nije zadat. Redosled po orijentaciji:
		/// Large → Large, Medium, Small; Medium → Medium, Small, Large (v2); Small → Small, Medium, Large. Zatim isto u drugoj
		/// orijentaciji.
		/// </summary>
		public static T? Select<T>(ScreenLayout layout, T? landscapeLarge, T? landscapeMedium, T? landscapeSmall,
								   T? portraitLarge, T? portraitMedium, T? portraitSmall) where T : class
		{
			static T? Pick(ScreenSize size, T? large, T? medium, T? small) => size switch {
				ScreenSize.Large => large ?? medium ?? small,
				ScreenSize.Medium => medium ?? small ?? large,
				_ => small ?? medium ?? large
			};

			return layout.IsPortrait
				? Pick(layout.Size, portraitLarge, portraitMedium, portraitSmall) ?? Pick(layout.Size, landscapeLarge, landscapeMedium, landscapeSmall)
				: Pick(layout.Size, landscapeLarge, landscapeMedium, landscapeSmall) ?? Pick(layout.Size, portraitLarge, portraitMedium, portraitSmall);
		}

		protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
		{
			base.OnPropertyChanged(change);
			if (change.Property == ScreenLayoutProperty
				|| change.Property == LandscapeLargeTemplateProperty || change.Property == LandscapeMediumTemplateProperty
				|| change.Property == LandscapeSmallTemplateProperty || change.Property == PortraitLargeTemplateProperty
				|| change.Property == PortraitMediumTemplateProperty || change.Property == PortraitSmallTemplateProperty) {
				ContentTemplate = Select(GetScreenLayout(this), LandscapeLargeTemplate, LandscapeMediumTemplate, LandscapeSmallTemplate,
										 PortraitLargeTemplate, PortraitMediumTemplate, PortraitSmallTemplate) ?? __empty;
			}
		}
	}
}