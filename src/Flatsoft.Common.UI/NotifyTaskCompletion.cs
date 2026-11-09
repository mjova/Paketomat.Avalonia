using System.ComponentModel;

namespace Flatsoft.Common.UI
{
	// https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/march/async-programming-patterns-for-asynchronous-mvvm-applications-data-binding

	
	public class NotifyTaskCompletion : INotifyPropertyChanged
	{
		public static readonly PropertyChangedEventArgs __statusArgs = new (nameof(Status));
		public static readonly PropertyChangedEventArgs __isCompletedArgs = new (nameof(IsCompleted));
		public static readonly PropertyChangedEventArgs __isNotCompletedArgs = new (nameof(IsNotCompleted));
		public static readonly PropertyChangedEventArgs __isCanceledArgs = new (nameof(IsCanceled));
		public static readonly PropertyChangedEventArgs __isFaultedArgs = new (nameof(IsFaulted));
		public static readonly PropertyChangedEventArgs __exceptionArgs = new (nameof(Exception));
		public static readonly PropertyChangedEventArgs __innerExceptionArgs = new (nameof(InnerException));
		public static readonly PropertyChangedEventArgs __errorMessageArgs = new (nameof(ErrorMessage));
		public static readonly PropertyChangedEventArgs __isSuccessfullyCompletedArgs = new (nameof(IsSuccessfullyCompleted));

		public NotifyTaskCompletion(Task task)
		{
			Task = task;
			if (!task.IsCompleted) {
				var _ = WatchTaskAsync(task);
			}
		}
		private async Task WatchTaskAsync(Task task)
		{
			try {
				await task;
			}
			catch {
			}
			UiThread.Raise(RaiseCompletionChanged);	// v4: na UI nit
		}

		private void RaiseCompletionChanged()
		{
			PropertyChanged?.Invoke(this, __statusArgs);
			PropertyChanged?.Invoke(this, __isCompletedArgs);
			PropertyChanged?.Invoke(this, __isNotCompletedArgs);
			if (Task.IsCanceled) {
				PropertyChanged?.Invoke(this, __isCanceledArgs);
			}
			else if (Task.IsFaulted) {
				PropertyChanged?.Invoke(this, __isFaultedArgs);
				PropertyChanged?.Invoke(this, __exceptionArgs);
				PropertyChanged?.Invoke(this, __innerExceptionArgs);
				PropertyChanged?.Invoke(this, __errorMessageArgs);
			}
			else {
				PropertyChanged?.Invoke(this, __isSuccessfullyCompletedArgs);
			}
		}
		public virtual Task Task { get; private set; }
		public TaskStatus Status => Task.Status; 
		public bool IsCompleted => Task.IsCompleted; 
		public bool IsNotCompleted => !Task.IsCompleted; 
		public bool IsSuccessfullyCompleted => Task.Status == TaskStatus.RanToCompletion;
		public bool IsCanceled => Task.IsCanceled; 
		public bool IsFaulted => Task.IsFaulted; 
		public AggregateException? Exception => Task.Exception;
		public Exception? InnerException => Exception?.InnerException;
		public string? ErrorMessage => InnerException?.Message;


		public event PropertyChangedEventHandler? PropertyChanged;
	}


	public sealed class NotifyTaskCompletion<TResult> : NotifyTaskCompletion
	{
		private readonly Task<TResult> _task;

		public NotifyTaskCompletion(Task<TResult> task)
			: base(task)
		{
			_task = task;
		}
		public override Task Task => _task;
	}
}
