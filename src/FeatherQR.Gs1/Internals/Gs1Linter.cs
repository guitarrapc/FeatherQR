namespace FeatherQR.Gs1.Internals;

/// <summary>
/// The linters the pinned GS1 Barcode Syntax Dictionary names on its components, one bit each in
/// <see cref="Gs1AiTable.Gs1Component.Linters"/>. The names are the dictionary's own, in alphabetical order, and
/// the generator test refuses a dictionary release that names a linter this enum lacks, so a new
/// linter cannot arrive in the table without its check.
/// </summary>
internal enum Gs1Linter : byte
{
    Couponcode,
    Couponposoffer,
    Csum,
    Csumalpha,
    Gcppos1,
    Gcppos2,
    Hasnondigit,
    Hh,
    Hhmi,
    Hyphen,
    Iban,
    Importeridx,
    Iso3166,
    Iso3166999,
    Iso3166alpha2,
    Iso4217,
    Iso5218,
    Latitude,
    Longitude,
    Mediatype,
    Mi,
    Nonzero,
    Nozeroprefix,
    Packagetype,
    Pcenc,
    Pieceoftotal,
    Posinseqslash,
    Ss,
    Winding,
    Yesno,
    Yymmd0,
    Yymmdd,
    Yyyymmdd,
    Zero,
}

/// <summary>The character repertoire of a component (GS1 General Specifications 7.11): digits, CSET 82, CSET 39 or CSET 64.</summary>
internal enum Gs1ComponentType : byte
{
    /// <summary><c>N</c>: digits.</summary>
    Numeric = 0,
    /// <summary><c>X</c>: CSET 82, a subset of printable ASCII.</summary>
    Cset82 = 1,
    /// <summary><c>Y</c>: CSET 39, digits, upper-case letters, <c>#</c>, <c>-</c> and <c>/</c>.</summary>
    Cset39 = 2,
    /// <summary><c>Z</c>: CSET 64, base64url with <c>=</c> padding.</summary>
    Cset64 = 3,
}
