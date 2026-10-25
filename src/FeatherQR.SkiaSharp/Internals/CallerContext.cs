using System.Runtime.CompilerServices;

namespace FeatherQR.SkiaSharp.Internals;

/// <summary>
/// Moves an async call off its caller's context before the call computes.
/// </summary>
/// <remarks>
/// An async method runs on its caller's thread until an await that does not complete at once, and a render comes before any.
/// Under a synchronization context or a task scheduler of the caller's own, as on a UI thread, the call moves to the thread pool, so the render leaves that thread.
/// With neither, as on a server, the await completes at once and the call stays on its thread.
/// </remarks>
internal static class CallerContext
{
    public static LeaveAwaitable Leave() => default;

    internal readonly struct LeaveAwaitable : ICriticalNotifyCompletion
    {
        private static readonly WaitCallback RunContinuation = static state => ((Action)state!).Invoke();

        public LeaveAwaitable GetAwaiter() => this;

        public bool IsCompleted => SynchronizationContext.Current is null && TaskScheduler.Current == TaskScheduler.Default;

        public void GetResult()
        {
        }

        public void OnCompleted(Action continuation) => ThreadPool.QueueUserWorkItem(RunContinuation, continuation);

        // The async method builder flows the execution context itself when it calls this one
        public void UnsafeOnCompleted(Action continuation) => ThreadPool.UnsafeQueueUserWorkItem(RunContinuation, continuation);
    }
}
