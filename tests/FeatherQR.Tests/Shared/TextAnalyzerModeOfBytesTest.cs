using System.Runtime.InteropServices;
using System.Text;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="TextAnalyzer.ModeOfBytes(ReadOnlySpan{byte})"/>, and its form over a reference and a length, against the mode <see cref="TextAnalyzer.Analyze(ReadOnlySpan{char}, EciMode)"/> gives the same bytes widened one to a char.
/// The bytes calls widen and analyze the payloads that can fit, and take this mode for the ones they refuse before that.
/// </summary>
public class TextAnalyzerModeOfBytesTest
{
    /// <summary>Every byte value alone, after digits and after letters, so each value meets each mode the bytes before it allowed.</summary>
    [Test]
    public async Task EveryByteValue_AfterEachMode_TakesTheModeOfTheAnalysis()
    {
        var mismatches = new List<string>();
        foreach (var prefix in new[] { "", "0123", "AB12" })
        {
            for (var value = 0; value < 256; value++)
            {
                byte[] bytes = [.. Encoding.ASCII.GetBytes(prefix), (byte)value];
                if (!BothFormsTakeTheModeOfTheAnalysis(bytes))
                    mismatches.Add($"\"{prefix}\" + 0x{value:X2}");
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task Empty_TakesTheModeOfTheAnalysis()
        => await Assert.That(BothFormsTakeTheModeOfTheAnalysis([])).IsTrue();

    /// <summary>Each payload draws from digits only, or adds some letters and the symbols of Alphanumeric mode, then any ASCII, then any byte, so every mode comes out.</summary>
    [Test]
    public async Task RandomPayloads_TakeTheModeOfTheAnalysis()
    {
        var random = new Random(1);
        var mismatches = new List<string>();
        for (var trial = 0; trial < 2000; trial++)
        {
            var kinds = random.Next(1, 5);
            var bytes = new byte[random.Next(1, 60)];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = random.Next(kinds) switch
                {
                    0 => (byte)('0' + random.Next(10)),
                    1 => (byte)"ABCXYZ $%*+-./:"[random.Next(15)],
                    2 => (byte)random.Next(128),
                    _ => (byte)random.Next(256),
                };
            }
            if (!BothFormsTakeTheModeOfTheAnalysis(bytes))
                mismatches.Add(Convert.ToHexString(bytes));
        }

        await Assert.That(mismatches).IsEmpty();
    }

    private static bool BothFormsTakeTheModeOfTheAnalysis(byte[] bytes)
    {
        var analyzed = TextAnalyzer.Analyze(Encoding.Latin1.GetString(bytes), EciMode.Default).EncodingMode;
        return TextAnalyzer.ModeOfBytes(bytes) == analyzed
            && TextAnalyzer.ModeOfBytes(ref MemoryMarshal.GetArrayDataReference(bytes), bytes.Length) == analyzed;
    }
}
