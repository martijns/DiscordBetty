using System;
using System.Threading.Tasks;

namespace Betty.Bot.Extensions
{
	/// <summary>
	/// The static TaskExtensions class is used to define extension to Task.
	/// </summary>
	public static class TaskExtensions
	{
		/// <summary>
		/// Forgets the specified task and only continues using the onError.
		/// </summary>
		/// <remarks>
		/// If you want to be sure your call won't block (i.e. due to CPU intensive work before the first 'await' keyword), use:
		/// Task.Run(async () => await MyMethodAsync()).Forget(..)
		/// </remarks>
		public static void Forget(this Task task, Action<Exception> onError = null, Action onSuccess = null)
		{
			// If already completed, don't waste CPU. Just return.
			if (task.IsCompleted && !task.IsCanceled && !task.IsFaulted)
			{
				onSuccess?.Invoke();
				return;
			}

			// If already faulted, don't waste CPU. Invoke the handler and return.
			if (task.IsFaulted || task.IsCanceled)
			{
				onError?.Invoke(task.Exception);
				return;
			}

			// Otherwise start, but on completion call this method again for further evaluation. And make sure it doesn't continue
			// on the same SynchronizationContext.
			task.ContinueWith(t =>
			{
				// It's now either completed or faulted.
				t.Forget(onError, onSuccess);
			}).ConfigureAwait(false);

			// Just in case nobody started the task yet, start it.
			if (task.Status == TaskStatus.Created)
			{
				task.Start();
			}
		}
	}
}
