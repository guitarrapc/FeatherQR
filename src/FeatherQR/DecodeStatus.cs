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

    /// <summary>The format information cannot be decoded or is inconsistent with the symbol.</summary>
    FormatInformationInvalid,

    /// <summary>One or more Reed-Solomon blocks contain more errors than the ECC level can correct.</summary>
    DataUncorrectable,

    /// <summary>The data bitstream is malformed (invalid segment values or truncated data).</summary>
    InvalidBitstream,

    /// <summary>
    /// The bitstream is well-formed but uses a feature this decoder does not support: an ECI assignment it does not map to a charset (it reads 1, 3 and 27 as ISO-8859-1, 26 as UTF-8 and 20 as Shift_JIS).
    /// A property of the symbol's structure, not of its text. See <see cref="UnmappedCharacter"/> for the per-character case.
    /// </summary>
    UnsupportedContent,

    /// <summary>The destination buffer is too small for the decoded text.</summary>
    DestinationTooSmall,

    /// <summary>No symbol was detected in the image (finder patterns not found or inconsistent).</summary>
    NotDetected,

    /// <summary>
    /// A character has no mapping in the character set used by this decoder.
    /// </summary>
    UnmappedCharacter,
}
