using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// Which corners of a finder triple the image decoder decodes from: the one the triangle's shape names, and after it only a corner whose two timing patterns read.
/// The corner steps are a recording stand-in, so what is asserted is the decoder's own decisions. The check before another corner changes no read an image can show, since a decode from a wrong corner fails anyway; what it saves is that decode, and only a recording can see it.
/// </summary>
public class FinderTripleCornersTest
{
    /// <summary>Centres 0 to 2 are a right isosceles triangle whose shape names vertex 0; 3 closes the square.</summary>
    private static readonly FinderPattern[] Points = [Finder(100, 100), Finder(300, 100), Finder(100, 300), Finder(300, 300)];

    private static FinderPattern Finder(float x, float y) => new() { X = x, Y = y, ModuleSize = 4f, Count = 3 };

    private sealed class Log
    {
        public List<string> Events { get; } = [];
    }

    /// <summary>A corner is named by its centre's index and the triple's, "2/012" for centre 2 as the top-left of centres 0, 1 and 2.</summary>
    private struct RecordingCorners : QRImageDecoder.ICornerAttempt
    {
        public Log Log;

        /// <summary>The corners whose timing patterns read.</summary>
        public string[] Reading;

        /// <summary>What a decode from a corner returns; a corner not listed fails.</summary>
        public Dictionary<string, DecodeStatus> Results;

        public readonly bool ReadsTimingPatterns(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft)
        {
            var corner = Name(topLeft, topRight, bottomLeft);
            Log.Events.Add($"timing {corner}");
            return Array.IndexOf(Reading, corner) >= 0;
        }

        public readonly DecodeStatus Decode(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, FinderPattern topLeft, FinderPattern topRight, FinderPattern bottomLeft, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
        {
            var corner = Name(topLeft, topRight, bottomLeft);
            Log.Events.Add($"decode {corner}");
            var status = Results.TryGetValue(corner, out var result) ? result : DecodeStatus.DataUncorrectable;
            charsWritten = 0;
            // The version field carries the corner's centre, so a test can tell whose diagnostics came back
            info = new QRCodeDecodeInfo(status, IndexOf(topLeft), default, -1, 0);
            return status;
        }

        private static string Name(in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft)
        {
            int[] members = [IndexOf(topLeft), IndexOf(topRight), IndexOf(bottomLeft)];
            Array.Sort(members);
            return $"{IndexOf(topLeft)}/{string.Concat(members)}";
        }

        private static int IndexOf(in FinderPattern finder)
        {
            for (var i = 0; i < Points.Length; i++)
            {
                if (Points[i].X == finder.X && Points[i].Y == finder.Y)
                    return i;
            }
            throw new InvalidOperationException($"No centre at ({finder.X}, {finder.Y})");
        }
    }

    [Test]
    [Arguments(DecodeStatus.Success)]
    [Arguments(DecodeStatus.DestinationTooSmall)]
    [Arguments(DecodeStatus.UnmappedCharacter)]
    [Arguments(DecodeStatus.UnsupportedContent)]
    public async Task ShapeCornerSettles_NoOtherCornerIsChecked(DecodeStatus result)
    {
        var (status, info, events) = DecodeTriple(reading: ["1/012", "2/012"], ("0/012", result), ("1/012", DecodeStatus.Success), ("2/012", DecodeStatus.Success));

        await Assert.That(status).IsEqualTo(result);
        await Assert.That(info.Version).IsEqualTo(0);
        await Assert.That(events).IsEqualTo("decode 0/012");
    }

    /// <summary>The check before a decode: a corner whose timing patterns do not read is not decoded, though here a decode from it would have read.</summary>
    [Test]
    public async Task ShapeCornerFails_ACornerWhoseTimingPatternsDoNotReadIsNotDecoded()
    {
        var (status, info, events) = DecodeTriple(reading: [], ("1/012", DecodeStatus.Success), ("2/012", DecodeStatus.Success));

        await Assert.That(status).IsEqualTo(DecodeStatus.DataUncorrectable);
        await Assert.That(info.Version).IsEqualTo(0);
        await Assert.That(events).IsEqualTo("decode 0/012, timing 1/012, timing 2/012");
    }

    [Test]
    public async Task ShapeCornerFails_TheCornerWhoseTimingPatternsReadIsDecoded()
    {
        var (status, info, events) = DecodeTriple(reading: ["2/012"], ("1/012", DecodeStatus.Success), ("2/012", DecodeStatus.Success));

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(info.Version).IsEqualTo(2);
        await Assert.That(events).IsEqualTo("decode 0/012, timing 1/012, timing 2/012, decode 2/012");
    }

    /// <summary>Another corner that reads its timing patterns and still fails leaves the shape corner's status and diagnostics, the first attempt's.</summary>
    [Test]
    public async Task NoCornerSettles_TheShapeCornersDiagnosticsStand()
    {
        var (status, info, events) = DecodeTriple(reading: ["2/012"], ("0/012", DecodeStatus.FormatInformationInvalid));

        await Assert.That(status).IsEqualTo(DecodeStatus.FormatInformationInvalid);
        await Assert.That(info.Version).IsEqualTo(0);
        await Assert.That(events).IsEqualTo("decode 0/012, timing 1/012, timing 2/012, decode 2/012");
    }

    /// <summary>
    /// The alternatives after a failed triple, here the two other triples of a square's corners: one whose timing patterns read from no corner is not decoded, though a decode from any of its corners would have read.
    /// </summary>
    [Test]
    public async Task Alternative_NoCornerReadsItsTimingPatterns_IsNotDecoded()
    {
        var (status, info, events) = DecodeAlternatives(reading: [], ("1/013", DecodeStatus.Success), ("3/013", DecodeStatus.Success), ("0/013", DecodeStatus.Success), ("2/023", DecodeStatus.Success), ("3/023", DecodeStatus.Success), ("0/023", DecodeStatus.Success));

        await Assert.That(status).IsEqualTo(DecodeStatus.DataUncorrectable);
        await Assert.That(info.Version).IsEqualTo(-1);
        await Assert.That(events).IsEqualTo("timing 1/013, timing 3/013, timing 0/013, timing 2/023, timing 3/023, timing 0/023");
    }

    /// <summary>An alternative whose shape names a corner that does not read its timing patterns is decoded from the one that does, and from no other.</summary>
    [Test]
    public async Task Alternative_ShapeCornerDoesNotRead_IsDecodedFromTheCornerThatDoes()
    {
        var (status, info, events) = DecodeAlternatives(reading: ["3/013"], ("1/013", DecodeStatus.Success), ("3/013", DecodeStatus.Success), ("0/013", DecodeStatus.Success));

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(info.Version).IsEqualTo(3);
        await Assert.That(events).IsEqualTo("timing 1/013, timing 3/013, decode 3/013");
    }

    private static (DecodeStatus Status, QRCodeDecodeInfo Info, string Events) DecodeTriple(string[] reading, params (string Corner, DecodeStatus Result)[] results)
    {
        var corners = Recording(reading, results);
        var status = QRImageDecoder.DecodeTriple(ref corners, default, 0, 0, 0, default, Points.AsSpan(0, 3), new char[16], out _, out var info);
        return (status, info, string.Join(", ", corners.Log.Events));
    }

    /// <summary>The selected triple (centres 0 to 2) failed, and the list holds centre 3 as well.</summary>
    private static (DecodeStatus Status, QRCodeDecodeInfo Info, string Events) DecodeAlternatives(string[] reading, params (string Corner, DecodeStatus Result)[] results)
    {
        var corners = Recording(reading, results);
        var candidates = Points.ToArray();
        var charsWritten = 0;
        var info = new QRCodeDecodeInfo(DecodeStatus.DataUncorrectable, -1, default, -1, 0);
        var status = QRImageDecoder.DecodeAlternativeTriples(ref corners, default, 0, 0, 0, default, candidates, Points.AsSpan(0, 3), new char[16], DecodeStatus.DataUncorrectable, ref charsWritten, ref info);
        return (status, info, string.Join(", ", corners.Log.Events));
    }

    private static RecordingCorners Recording(string[] reading, (string Corner, DecodeStatus Result)[] results)
        => new() { Log = new Log(), Reading = reading, Results = results.ToDictionary(static r => r.Corner, static r => r.Result) };
}
