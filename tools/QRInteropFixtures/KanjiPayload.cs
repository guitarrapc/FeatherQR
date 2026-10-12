using System.Text;

namespace QRInteropFixtures;

/// <summary>
/// Shift_JIS payload plumbing for Kanji-mode fixtures.
///
/// qrtool's <c>--mode kanji</c> takes the payload as raw Shift_JIS bytes (UTF-8
/// input fails with "invalid character"), so the corpus has to hand it bytes, not
/// text. For ordinary payloads CP932 produces them, but for the seven cells where
/// JIS X 0208 and CP932 disagree it cannot, because .NET has no encoder for the
/// JIS X 0208 reading (U+301C is not in CP932 at all). Those cases carry their
/// bytes explicitly instead.
/// </summary>
public static class KanjiPayload
{
    public const string ModeName = "Kanji";

    private static Encoding? shiftJis;

    /// <summary>
    /// CP932 that THROWS on an unencodable character instead of substituting '?'.
    /// </summary>
    /// <remarks>
    /// Registering the provider is also what makes ZXing.Net able to resolve Shift_JIS
    /// at all, so touching this property early is load-bearing beyond its return value.
    /// </remarks>
    public static Encoding ShiftJis
    {
        get
        {
            if (shiftJis is null)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                shiftJis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
            }
            return shiftJis;
        }
    }

    /// <summary>
    /// The bytes to hand an encoder for this case: the explicit ones when the case
    /// pins them, otherwise the CP932 encoding of the payload text.
    /// </summary>
    /// <remarks>
    /// Detection of an unencodable character is the encoder's job, not a scan of the
    /// output for '?'. That scan cannot work: CP932 substitutes '?' for what it cannot
    /// encode, so a payload that legitimately contains '?' makes the substitution
    /// indistinguishable from the real thing, and the corpus would ship a fixture
    /// asserting text the symbol does not carry, exactly the failure the corpus exists
    /// to catch.
    /// </remarks>
    public static byte[] ToShiftJisBytes(string payloadText, string? shiftJisHex)
    {
        if (shiftJisHex is not null)
            return Convert.FromHexString(shiftJisHex);

        try
        {
            return ShiftJis.GetBytes(payloadText);
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidOperationException(
                $"CP932 cannot encode '{ex.CharUnknown}' in \"{payloadText}\"; pin the Shift_JIS bytes on the case with ShiftJisHex instead.", ex);
        }
    }
}
