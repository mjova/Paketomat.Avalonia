using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace TopPos.Kiosk.UI.Tests
{
	/// <summary>Avalonia UI nit za jezgro (redosled obaveštenja, čekanje zatvaranja kontrolera uz obradu poruka).</summary>
	public class AvaloniaUiDispatcherTests
	{
		/// <summary>
		/// Zatvaranje kontrolera čeka nastavak koji treba UI nit (komanda pokrenuta sa nje): bez ugnežđene petlje bi se zaglavilo
		/// (v2 DispatcherFrame, LockControllerShutdown).
		/// </summary>
		[AvaloniaFact]
		public void Wait_on_ui_thread_processes_ui_work_until_task_completes()
		{
			var dispatcher = new AvaloniaUiDispatcher();
			var uiWorkDone = new TaskCompletionSource();
			var task = Task.Run(async () => {
				await Dispatcher.UIThread.InvokeAsync(() => uiWorkDone.SetResult());
				await uiWorkDone.Task;
			});

			dispatcher.WaitWithMessagePump(task);

			Assert.True(task.IsCompletedSuccessfully);
		}

		[AvaloniaFact]
		public void Post_and_Invoke_from_background_keep_order()
		{
			var dispatcher = new AvaloniaUiDispatcher();
			var order = new List<int>();
			var background = Task.Run(() => {
				dispatcher.Post(() => order.Add(1));
				dispatcher.Post(() => order.Add(2));
				dispatcher.Invoke(() => order.Add(3));
			});

			dispatcher.WaitWithMessagePump(background);

			Assert.Equal([1, 2, 3], order);
		}
	}
}