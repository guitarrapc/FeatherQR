using System.Runtime.CompilerServices;

namespace FeatherQR.Internals;

/// <summary>
/// The refusals the three Kanji writers share.
/// A writer meets a character without an encoder cell, or a Kanji run under an ECI header, only when the analysis or a plan let it through, so these report the defect instead of writing a symbol that reads back as other text.
/// </summary>
internal static class KanjiCells
{
    /// <summary>Throws for the first character of <paramref name="chars"/> that <see cref="ShiftJisKanjiReverseTable"/> has no cell for.</summary>
    /// <remarks>Outlined so the writers' loops keep only an OR and a sign test; the scan runs on the failure path alone.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowCharacterWithoutCell(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
        {
            if (ShiftJisKanjiReverseTable.Lookup(c) < 0)
                throw new ArgumentException($"U+{(int)c:X4} has no Kanji-mode cell (JIS X 0208 without the cells CP932 reads differently), so it cannot be written in Kanji mode.", nameof(chars));
        }

        throw new ArgumentException("A Kanji run was refused although every character has a cell.", nameof(chars));
    }

    /// <summary>Kanji beside an ECI header is not written: whether readers apply JIS X 0208 to a Kanji segment that follows one has not been measured.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowKanjiUnderEci(EciMode eci)
        => throw new ArgumentException($"A Kanji run is written only in a stream without an ECI header, not under {eci}.", nameof(eci));
}
