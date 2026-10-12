using FeatherQR.Gs1;

namespace FeatherQR.Tests;

/// <summary>
/// The GS1 data layer allocates nothing on its span paths (gs1-support-plan.md, "What has to stay true"):
/// <see cref="Gs1ElementString.TryValidate"/> on an accepted and on a refused element string, and a
/// <c>foreach</c> over <see cref="Gs1ElementString.EnumerateElements"/>. Release only, as
/// <see cref="DecodeAllocationTest"/> is, since a Debug build allocates where an optimized one does not.
/// </summary>
[NotInParallel]
public class Gs1AllocationTest
{
#if !DEBUG
    private const string Photograph = "0109504000059101" + "2112345678p901" + "\u001D" + "101234567p" + "\u001D" + "17141120" + "8200http://www.gs1.org/demo/";

    private static long SteadyStateBytes(Action call)
    {
        for (var i = 0; i < 3; i++)
            call();
        var fewest = long.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 4; i++)
                call();
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return fewest;
    }

    [Test]
    public async Task TryValidate_AllocatesNothing()
    {
        var accepted = Photograph;
        var refused = "0109520123456788" + "10ABC" + "\u001D" + "10DEF";
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryValidate(accepted.AsSpan(), out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryValidate(refused.AsSpan(), out _))).IsEqualTo(0);
    }

    [Test]
    public async Task EnumerateElements_AllocatesNothing()
    {
        var text = Photograph;
        await Assert.That(SteadyStateBytes(() =>
        {
            var count = 0;
            foreach (var element in Gs1ElementString.EnumerateElements(text.AsSpan()))
                count += element.Value.Length;
            if (count == 0)
                throw new InvalidOperationException();
        })).IsEqualTo(0);
    }
#else
    [Test]
    [Skip("Release only: a Debug build allocates where an optimized one does not.")]
    public Task AllocationTests_RunInReleaseOnly() => Task.CompletedTask;
#endif
}
