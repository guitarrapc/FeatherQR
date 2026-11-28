using System.Buffers;
using System.Diagnostics.Tracing;

namespace FeatherQR.Tests;

/// <summary>
/// Watches the rents and returns of <see cref="ArrayPool{T}.Shared"/> for bytes on the thread that created it, through the pool's event source.
/// </summary>
/// <remarks>
/// The pool's thread slot holds one array a size. A second array of a size rented while the first is out comes from the per-core stacks every thread shares,
/// where a thread renting beside this one can take it and the rent allocates. <see cref="MostOutOfOneSize"/> is the most arrays of one length this thread had out at once.
/// Arrays are told apart by length, since every array of a bucket has the bucket's length, and the event names no bucket for an array the pool allocates.
/// The event source calls the listener on the thread that rents or returns, so rents on other threads are left out by their thread id.
/// </remarks>
internal sealed class ArrayPoolRentWatch : EventListener
{
    private const int BufferRentedEvent = 1;
    private const int BufferReturnedEvent = 3;

    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly int _poolId = ArrayPool<byte>.Shared.GetHashCode();
    private readonly Dictionary<int, int> _lengthOf = [];
    private readonly Dictionary<int, int> _outOfLength = [];
    private volatile bool _watching;

    public int MostOutOfOneSize { get; private set; }

    public ArrayPoolRentWatch() => _watching = true;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "System.Buffers.ArrayPoolEventSource")
            EnableEvents(source, EventLevel.Verbose);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!_watching || Environment.CurrentManagedThreadId != _threadId || e.Payload is null || (int)e.Payload[2]! != _poolId)
            return;
        var bufferId = (int)e.Payload[0]!;
        if (e.EventId == BufferRentedEvent)
        {
            var length = (int)e.Payload[1]!;
            _lengthOf[bufferId] = length;
            var outNow = _outOfLength.TryGetValue(length, out var n) ? n + 1 : 1;
            _outOfLength[length] = outNow;
            MostOutOfOneSize = Math.Max(MostOutOfOneSize, outNow);
        }
        else if (e.EventId == BufferReturnedEvent && _lengthOf.Remove(bufferId, out var length))
        {
            _outOfLength[length]--;
        }
    }

    public override void Dispose()
    {
        _watching = false;
        base.Dispose();
    }
}
