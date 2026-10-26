namespace FeatherQR.Tests;

/// <summary>
/// Where an async call is made from, for the cases where it leaves its caller to compute.
/// </summary>
public enum Caller
{
    /// <summary>A thread with a synchronization context of one thread, as a UI thread has.</summary>
    SynchronizationContext,

    /// <summary>A task run by a task scheduler of the caller's own, with no synchronization context.</summary>
    TaskScheduler,
}

internal static class Callers
{
    /// <summary>Runs <paramref name="body"/> as <paramref name="caller"/>, and awaits it there.</summary>
    public static async Task RunUnder(Caller caller, Func<Task> body)
    {
        if (caller == Caller.SynchronizationContext)
        {
            using var context = new SingleThreadSynchronizationContext();
            await context.Run(body);
        }
        else
        {
            var scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
            await Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.None, scheduler).Unwrap();
        }
    }
}
