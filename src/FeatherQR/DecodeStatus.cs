namespace FeatherQR;

/// <summary>
/// Result status of a decode attempt, shared by the Standard QR, Micro QR and rMQR decoders.
/// </summary>
public enum DecodeStatus
{
    /// <summary>Decoding succeeded.</summary>
    Success = 0,

    /// <summary>The input is not a valid module matrix (invalid size or no dark modules).</summary>
    InvalidMatrix,

    /// <summary>Both format information copies are corrupted beyond BCH correction capacity.</summary>
    FormatInformationInvalid,

    /// <summary>One or more Reed-Solomon blocks contain more errors than the ECC level can correct.</summary>
    DataUncorrectable,

    /// <summary>The data bitstream is malformed (invalid segment values or truncated data).</summary>
    InvalidBitstream,

    /// <summary>
    /// The bitstream is well-formed but uses a feature this decoder does not support (FNC1, Structured Append, or an unsupported ECI charset).
    /// A property of the symbol's structure, not of its text; see <see cref="UnmappedCharacter"/> for the per-character case.
    /// </summary>
    UnsupportedContent,

    /// <summary>The destination buffer is too small for the decoded text.</summary>
    DestinationTooSmall,

    /// <summary>No symbol was detected in the image (finder patterns not found or inconsistent).</summary>
    NotDetected,

    /// <summary>
    /// The symbol was read correctly, but one character has no mapping under the charset this decoder applies, so no text is produced.
    /// </summary>
    /// <remarks>
    /// Today this means a Kanji mode cell outside JIS X 0208 — in practice one of the 83 characters Microsoft CP932 adds in NEC row 13 (circled digits, roman numerals, unit ligatures).
    /// It is deliberately distinct from <see cref="UnsupportedContent"/>: that says the symbol uses a feature this library does not implement, and no reader choice changes it, whereas this says the symbol is well formed and a CP932-capable reader would read it.
    /// A caller that wants to fall back to such a reader should branch on exactly this status.
    /// </remarks>
    UnmappedCharacter,
}
