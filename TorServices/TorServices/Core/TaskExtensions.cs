using System;
using System.Threading.Tasks;

namespace TorServices.Core;

public static class TaskExtensions
{
    /// <summary>
    /// Safely executes a task without awaiting it, ensuring exceptions are observed and logged.
    /// </summary>
    public static void FireAndForget(this Task task, string? operationName = null)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted) ObserveException(task, operationName);
            return;
        }

        task.ContinueWith(t =>
        {
            if (t.IsFaulted) ObserveException(t, operationName);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private static void ObserveException(Task task, string? operationName)
    {
        var ex = task.Exception?.Flatten().InnerException ?? task.Exception;
        if (ex != null)
        {
            // We use Console.WriteLine here as a fallback if AppLogger isn't accessible 
            // but in this project we can probably use AppLogger if we make it accessible or just rely on TaskScheduler.UnobservedTaskException which is already hooked up.
            // However, by observing it here, we prevent it from reaching the finalizer.
            Console.WriteLine($"[Task Error] {operationName ?? "Async Operation"}: {ex.Message}");
        }
    }
}
