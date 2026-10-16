using System.Buffers;
using System.Diagnostics.Tracing;

namespace FeatherQR.Tests;

/// <summary>
/// Counts the arrays the shared pool of characters hands out to the thread that made the listener, and those it takes
/// back. A test that makes one runs apart from the other tests of its process. Once a listener asks for the pool's
/// events, every thread raises them, and a test that counts its own allocations would count the events' too.
/// </summary>
internal sealed class CharPoolRents : EventListener
{
    // Set before the base constructor runs, which already reports the event sources that exist.
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly int _pool = ArrayPool<char>.Shared.GetHashCode();
    private int _rented;
    private int _returned;

    /// <summary>The arrays a call rents and returns, and its answer.</summary>
    public (int Rented, int Returned) During(Func<bool> call, out bool answer)
    {
        Volatile.Write(ref _rented, 0);
        Volatile.Write(ref _returned, 0);
        answer = call();
        return (Volatile.Read(ref _rented), Volatile.Read(ref _returned));
    }

    /// <summary>
    /// Fills the array the pool hands this thread next for the length with a character no caller expects. The
    /// pool keeps the array a thread returned last for that thread's next rent of its size, so a result written short
    /// of its end shows this character instead of what an earlier test left there.
    /// </summary>
    public static void FillNextRent(int length)
    {
        var array = ArrayPool<char>.Shared.Rent(length);
        array.AsSpan().Fill('\uFFFF');
        ArrayPool<char>.Shared.Return(array);
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "System.Buffers.ArrayPoolEventSource")
            EnableEvents(eventSource, EventLevel.Verbose);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        // A rent's payload is the array's id, its length, the pool's id and the bucket. A return's is the first three.
        if (eventData.EventName is not ("BufferRented" or "BufferReturned") || Environment.CurrentManagedThreadId != _thread
            || eventData.Payload is not { Count: > 2 } payload || Convert.ToInt32(payload[2]) != _pool)
            return;
        if (eventData.EventName == "BufferRented")
            Interlocked.Increment(ref _rented);
        else
            Interlocked.Increment(ref _returned);
    }
}
