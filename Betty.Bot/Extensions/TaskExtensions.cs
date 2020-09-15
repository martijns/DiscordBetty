using System;
using System.Threading.Tasks;

namespace Betty.Bot.Extensions
{
    public static class TaskExtensions
    {
        public static void Forget(this Task task, Action<Exception> onError = null)
        {
            // If already completed, don't waste CPU. Just return.
            if (task.IsCompleted)
            {
                return;
            }

            // If already faulted, don't waste CPU. Invoke the handler and return.
            if (task.IsFaulted)
            {
                onError?.Invoke(task.Exception);
                return;
            }

            // Otherwise start, but on completion call this method again for further evaluation. And make sure it doesn't continue
            // on the same SynchronizationContext.
            task.ContinueWith(t =>
            {
                // It's now either completed or faulted.
                t.Forget(onError);
            }).ConfigureAwait(false);

            // Just in case nobody started the task yet, start it.
            if (task.Status == TaskStatus.Created)
            {
                task.Start();
            }
        }
    }
}
