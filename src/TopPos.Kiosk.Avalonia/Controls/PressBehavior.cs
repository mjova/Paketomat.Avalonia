using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace TopPos.Kiosk.UI.Controls
{
	/// <summary>
	/// Kratak i dug pritisak na dugme (v2 Flatsoft.Common.UI ButtonPressBehavior: mouse događaji i DispatcherTimer). Pointer
	/// događaji pokrivaju i touch i miš. Dug pritisak se okida posle <see cref="LongPressThresholdProperty"/> ms samo ako je
	/// dugme i dalje pritisnuto i omogućeno (Home se gasi pri ulasku u plaćanje); posle njega otpuštanje ne okida kratak.
	/// Prekinut pritisak (izgubljen capture, npr. drugi prst ili sistem) ne okida ništa.
	/// </summary>
	public sealed class PressBehavior : AvaloniaObject
	{
		public static readonly AttachedProperty<ICommand?> ShortPressCommandProperty =
			AvaloniaProperty.RegisterAttached<PressBehavior, Button, ICommand?>("ShortPressCommand");

		public static readonly AttachedProperty<ICommand?> LongPressCommandProperty =
			AvaloniaProperty.RegisterAttached<PressBehavior, Button, ICommand?>("LongPressCommand");

		public static readonly AttachedProperty<int> LongPressThresholdProperty =
			AvaloniaProperty.RegisterAttached<PressBehavior, Button, int>("LongPressThreshold", 1000);

		private static readonly AttachedProperty<PressState?> __stateProperty =
			AvaloniaProperty.RegisterAttached<PressBehavior, Button, PressState?>("State");

		static PressBehavior()
		{
			ShortPressCommandProperty.Changed.AddClassHandler<Button>((button, _) => Attach(button));
			LongPressCommandProperty.Changed.AddClassHandler<Button>((button, _) => Attach(button));
		}

		public static ICommand? GetShortPressCommand(Button button) => button.GetValue(ShortPressCommandProperty);
		public static void SetShortPressCommand(Button button, ICommand? value) => button.SetValue(ShortPressCommandProperty, value);
		public static ICommand? GetLongPressCommand(Button button) => button.GetValue(LongPressCommandProperty);
		public static void SetLongPressCommand(Button button, ICommand? value) => button.SetValue(LongPressCommandProperty, value);
		public static int GetLongPressThreshold(Button button) => button.GetValue(LongPressThresholdProperty);
		public static void SetLongPressThreshold(Button button, int value) => button.SetValue(LongPressThresholdProperty, value);

		private static void Attach(Button button)
		{
			button.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
			button.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
			button.RemoveHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
			button.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
			button.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
			button.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
		}

		private static void OnPressed(object? sender, PointerPressedEventArgs e)
		{
			if (sender is not Button button || !button.IsEffectivelyEnabled) {
				return;
			}
			button.GetValue(__stateProperty)?.Stop();
			var state = new PressState(TimeSpan.FromMilliseconds(GetLongPressThreshold(button)));
			state.Timer.Tick += (_, _) => {
				state.Timer.Stop();
				if (state.Pressed && button.IsEffectivelyEnabled) {
					state.LongPressTriggered = true;
					Execute(GetLongPressCommand(button));
				}
			};
			button.SetValue(__stateProperty, state);
			state.Timer.Start();
		}

		private static void OnReleased(object? sender, PointerReleasedEventArgs e)
		{
			if (sender is not Button button || button.GetValue(__stateProperty) is not { } state) {
				return;
			}
			bool shortPress = state.Pressed && !state.LongPressTriggered && button.IsEffectivelyEnabled;
			state.Stop();
			if (shortPress) {
				Execute(GetShortPressCommand(button));
			}
		}

		private static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
		{
			if (sender is Button button && button.GetValue(__stateProperty) is { } state) {
				state.Stop();
			}
		}

		private static void Execute(ICommand? command)
		{
			if (command?.CanExecute(null) == true) {
				command.Execute(null);
			}
		}

		private sealed class PressState(TimeSpan threshold)
		{
			public DispatcherTimer Timer { get; } = new() { Interval = threshold };
			public bool Pressed { get; private set; } = true;
			public bool LongPressTriggered { get; set; }

			public void Stop()
			{
				Pressed = false;
				Timer.Stop();
			}
		}
	}
}