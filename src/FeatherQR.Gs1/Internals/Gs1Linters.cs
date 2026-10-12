namespace FeatherQR.Gs1.Internals;

/// <summary>
/// The linters the Syntax Dictionary names, as rules of the GS1 General Specifications and the standards it
/// cites, implemented here from those rules. Each takes one component's value and returns
/// <see cref="Gs1ErrorKind.None"/> or the kind its failure maps to (a check digit, a date or time, or
/// another value rule), with the place in the value. The reference linters' unit tests, imported with the
/// dictionary, are the oracle in <c>Gs1LinterTest</c>.
/// </summary>
internal static partial class Gs1Linters
{
    /// <summary>Runs one linter. <paramref name="year"/> resolves two-digit years (General Specifications 7.12, a window of -49 to +50 years).</summary>
    public static Gs1ErrorKind Check(Gs1Linter linter, ReadOnlySpan<char> value, int year, out int offset, out int length)
    {
        offset = 0;
        length = value.Length;
        switch (linter)
        {
            case Gs1Linter.Csum: return Csum(value, ref offset, ref length);
            case Gs1Linter.Csumalpha: return Csumalpha(value, ref offset, ref length);
            case Gs1Linter.Gcppos1: return Gcp(value, 0, ref offset, ref length);
            case Gs1Linter.Gcppos2: return Gcp(value, 1, ref offset, ref length);
            case Gs1Linter.Hasnondigit: return AllDigits(value, out _) ? Gs1ErrorKind.InvalidValue : Gs1ErrorKind.None;
            case Gs1Linter.Hh: return Clock(value, 23, ref offset, ref length);
            case Gs1Linter.Mi: return Clock(value, 59, ref offset, ref length);
            case Gs1Linter.Ss: return Clock(value, 59, ref offset, ref length);
            case Gs1Linter.Hhmi: return Hhmi(value, ref offset, ref length);
            case Gs1Linter.Hyphen: return Hyphen(value, ref offset, ref length);
            case Gs1Linter.Iban: return Iban(value, ref offset, ref length);
            case Gs1Linter.Importeridx: return value.Length == 1 && Gs1Charsets.IsCset64(value[0]) ? Gs1ErrorKind.None : Gs1ErrorKind.InvalidValue;
            case Gs1Linter.Iso3166: return Numeric3(value, ListKind.Iso3166);
            case Gs1Linter.Iso3166999: return Numeric3(value, ListKind.Iso3166Or999);
            case Gs1Linter.Iso3166alpha2: return value.Length == 2 && Gs1CodeLists.IsIso3166Alpha2(value[0], value[1]) ? Gs1ErrorKind.None : Gs1ErrorKind.InvalidValue;
            case Gs1Linter.Iso4217: return Numeric3(value, ListKind.Iso4217);
            case Gs1Linter.Iso5218: return value.Length == 1 && value[0] is '0' or '1' or '2' or '9' ? Gs1ErrorKind.None : Gs1ErrorKind.InvalidValue;
            case Gs1Linter.Latitude: return Coordinate(value, 1_800_000_000L, ref offset, ref length);
            case Gs1Linter.Longitude: return Coordinate(value, 3_600_000_000L, ref offset, ref length);
            case Gs1Linter.Mediatype: return value.Length == 2 && AllDigits(value, out _) && Gs1CodeLists.IsMediaType((value[0] - '0') * 10 + (value[1] - '0')) ? Gs1ErrorKind.None : Gs1ErrorKind.InvalidValue;
            case Gs1Linter.Nonzero: return Nonzero(value, ref offset, ref length);
            case Gs1Linter.Nozeroprefix: return Nozeroprefix(value, ref offset, ref length);
            case Gs1Linter.Packagetype: return Gs1CodeLists.IsPackageType(value) ? Gs1ErrorKind.None : Gs1ErrorKind.InvalidValue;
            case Gs1Linter.Pcenc: return Pcenc(value, ref offset, ref length);
            case Gs1Linter.Pieceoftotal: return Pieceoftotal(value, ref offset, ref length);
            case Gs1Linter.Posinseqslash: return Posinseqslash(value, ref offset, ref length);
            case Gs1Linter.Winding: return value.Length == 1 && value[0] is '0' or '1' or '9' ? Gs1ErrorKind.None : Gs1ErrorKind.InvalidValue;
            case Gs1Linter.Yesno: return value.Length == 1 && value[0] is '0' or '1' ? Gs1ErrorKind.None : Gs1ErrorKind.InvalidValue;
            case Gs1Linter.Yymmd0: return Yymmdd(value, year, allowDay00: true, ref offset, ref length);
            case Gs1Linter.Yymmdd: return Yymmdd(value, year, allowDay00: false, ref offset, ref length);
            case Gs1Linter.Yyyymmdd: return Yyyymmdd(value, ref offset, ref length);
            case Gs1Linter.Zero: return Zero(value, ref offset, ref length);
            case Gs1Linter.Couponcode: return Couponcode(value, year, ref offset, ref length);
            case Gs1Linter.Couponposoffer: return Couponposoffer(value, ref offset, ref length);
            default: return Gs1ErrorKind.InvalidValue;
        }
    }

    private enum ListKind { Iso3166, Iso3166Or999, Iso4217 }

    /// <summary>The GS1 mod-10 check digit (General Specifications 7.9.1): weights 3 and 1 from the right over the data, the check digit last.</summary>
    private static Gs1ErrorKind Csum(ReadOnlySpan<char> value, ref int offset, ref int length)
    {
        if (!AllDigits(value, out var bad))
        {
            offset = bad;
            length = 1;
            return Gs1ErrorKind.InvalidCharacter;
        }
        if (value.Length < 2)
            return Gs1ErrorKind.InvalidCheckDigit;
        var sum = 0;
        var weight = 3;
        for (var i = value.Length - 2; i >= 0; i--)
        {
            sum += (value[i] - '0') * weight;
            weight = 4 - weight;
        }
        var expected = (10 - sum % 10) % 10;
        if (value[value.Length - 1] - '0' == expected)
            return Gs1ErrorKind.None;
        offset = value.Length - 1;
        length = 1;
        return Gs1ErrorKind.InvalidCheckDigit;
    }

    /// <summary>The check character pair (General Specifications 7.9.5): CSET 82 reference values weighted by primes from the right, mod 1021, written as two characters of a 32-character set.</summary>
    private static Gs1ErrorKind Csumalpha(ReadOnlySpan<char> value, ref int offset, ref int length)
    {
        if (value.Length < 2 || value.Length - 2 > Primes.Length)
            return Gs1ErrorKind.InvalidCheckDigit;
        var data = value.Length - 2;
        var sum = 0;
        for (var i = 0; i < data; i++)
        {
            var reference = Gs1Charsets.Cset82Value(value[i]);
            if (reference < 0)
            {
                offset = i;
                length = 1;
                return Gs1ErrorKind.InvalidCharacter;
            }
            sum += reference * Primes[data - 1 - i];
        }
        sum %= 1021;
        const string Cset32 = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        if (value[data] == Cset32[sum >> 5] && value[data + 1] == Cset32[sum & 31])
            return Gs1ErrorKind.None;
        offset = data;
        length = 2;
        return Gs1ErrorKind.InvalidCheckDigit;
    }

    /// <summary>The first 97 primes: the weights of the check character pair, the rightmost data character taking 2.</summary>
    private static ReadOnlySpan<ushort> Primes =>
    [
        2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71, 73, 79, 83, 89,
        97, 101, 103, 107, 109, 113, 127, 131, 137, 139, 149, 151, 157, 163, 167, 173, 179, 181, 191, 193, 197, 199, 211, 223,
        227, 229, 233, 239, 241, 251, 257, 263, 269, 271, 277, 281, 283, 293, 307, 311, 313, 317, 331, 337, 347, 349, 353, 359,
        367, 373, 379, 383, 389, 397, 401, 409, 419, 421, 431, 433, 439, 443, 449, 457, 461, 463, 467, 479, 487, 491, 499, 503,
        509,
    ];

    /// <summary>A GS1 Company Prefix starts at <paramref name="from"/>: at least four digits, the shortest prefix GS1 allocates.</summary>
    private static Gs1ErrorKind Gcp(ReadOnlySpan<char> value, int from, ref int offset, ref int length)
    {
        const int MinimumLength = 4;
        if (value.Length < from + MinimumLength)
            return Gs1ErrorKind.InvalidValue;
        for (var i = from; i < from + MinimumLength; i++)
        {
            if (!Gs1Charsets.IsDigit(value[i]))
            {
                offset = i;
                length = 1;
                return Gs1ErrorKind.InvalidValue;
            }
        }
        return Gs1ErrorKind.None;
    }

    private static Gs1ErrorKind Clock(ReadOnlySpan<char> value, int max, ref int offset, ref int length)
    {
        if (value.Length != 2)
            return Gs1ErrorKind.InvalidDate;
        if (!AllDigits(value, out var bad))
        {
            offset = bad;
            length = 1;
            return Gs1ErrorKind.InvalidCharacter;
        }
        return Number(value) <= max ? Gs1ErrorKind.None : Gs1ErrorKind.InvalidDate;
    }

    private static Gs1ErrorKind Hhmi(ReadOnlySpan<char> value, ref int offset, ref int length)
    {
        if (value.Length != 4)
            return Gs1ErrorKind.InvalidDate;
        if (!AllDigits(value, out var bad))
        {
            offset = bad;
            length = 1;
            return Gs1ErrorKind.InvalidCharacter;
        }
        if (Number(value.Slice(0, 2)) > 23)
        {
            length = 2;
            return Gs1ErrorKind.InvalidDate;
        }
        if (Number(value.Slice(2, 2)) > 59)
        {
            offset = 2;
            length = 2;
            return Gs1ErrorKind.InvalidDate;
        }
        return Gs1ErrorKind.None;
    }

    private static Gs1ErrorKind Hyphen(ReadOnlySpan<char> value, ref int offset, ref int length)
    {
        if (value.Length == 0)
            return Gs1ErrorKind.InvalidValue;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '-')
            {
                offset = i;
                length = 1;
                return Gs1ErrorKind.InvalidValue;
            }
        }
        return Gs1ErrorKind.None;
    }

    /// <summary>ISO 13616: a country code, two check digits and up to 30 alphanumeric characters, whose rearrangement is 1 mod 97. The reference linter wants more than ten characters in all.</summary>
    private static Gs1ErrorKind Iban(ReadOnlySpan<char> value, ref int offset, ref int length)
    {
        if (value.Length <= 10 || value.Length > 34)
            return Gs1ErrorKind.InvalidValue;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if ((uint)(c - 'A') > 25 && (i < 2 || !Gs1Charsets.IsDigit(c)))
            {
                offset = i;
                length = 1;
                return Gs1ErrorKind.InvalidValue;
            }
        }
        // The country code is an ISO 3166 alpha-2 code.
        if (!Gs1CodeLists.IsIso3166Alpha2(value[0], value[1]))
        {
            length = 2;
            return Gs1ErrorKind.InvalidValue;
        }
        // Move the first four characters to the end and reduce digit by digit, letters as 10 to 35.
        var remainder = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[(i + 4) % value.Length];
            if (Gs1Charsets.IsDigit(c))
                remainder = (remainder * 10 + (c - '0')) % 97;
            else
                remainder = (remainder * 100 + (c - 'A' + 10)) % 97;
        }
        if (remainder == 1)
            return Gs1ErrorKind.None;
        offset = 2;
        length = 2;
        return Gs1ErrorKind.InvalidValue;
    }

    private static Gs1ErrorKind Numeric3(ReadOnlySpan<char> value, ListKind list)
    {
        if (value.Length != 3 || !AllDigits(value, out _))
            return Gs1ErrorKind.InvalidValue;
        var code = Number(value);
        var inList = list switch
        {
            ListKind.Iso3166 => Gs1CodeLists.IsIso3166Numeric(code),
            ListKind.Iso3166Or999 => code == 999 || Gs1CodeLists.IsIso3166Numeric(code),
            _ => Gs1CodeLists.IsIso4217(code),
        };
        return inList ? Gs1ErrorKind.None : Gs1ErrorKind.InvalidValue;
    }

    private static Gs1ErrorKind Coordinate(ReadOnlySpan<char> value, long max, ref int offset, ref int length)
    {
        if (value.Length != 10)
            return Gs1ErrorKind.InvalidValue;
        if (!AllDigits(value, out var bad))
        {
            offset = bad;
            length = 1;
            return Gs1ErrorKind.InvalidCharacter;
        }
        long n = 0;
        foreach (var c in value)
            n = n * 10 + (c - '0');
        return n <= max ? Gs1ErrorKind.None : Gs1ErrorKind.InvalidValue;
    }

    private static Gs1ErrorKind Nonzero(ReadOnlySpan<char> value, ref int offset, ref int length)
    {
        if (value.Length == 0)
            return Gs1ErrorKind.InvalidValue;
        if (!AllDigits(value, out var bad))
        {
            offset = bad;
            length = 1;
            return Gs1ErrorKind.InvalidCharacter;
        }
        foreach (var c in value)
        {
            if (c != '0')
                return Gs1ErrorKind.None;
        }
        return Gs1ErrorKind.InvalidValue;
    }

    private static Gs1ErrorKind Nozeroprefix(ReadOnlySpan<char> value, ref int offset, ref int length)
    {
        if (value.Length == 0)
            return Gs1ErrorKind.None;
        if (!AllDigits(value, out var bad))
        {
            offset = bad;
            length = 1;
            return Gs1ErrorKind.InvalidCharacter;
        }
        if (value[0] == '0')
        {
            length = 1;
            return Gs1ErrorKind.InvalidValue;
        }
        return Gs1ErrorKind.None;
    }

    private static Gs1ErrorKind Zero(ReadOnlySpan<char> value, ref int offset, ref int length)
    {
        if (value.Length == 0)
            return Gs1ErrorKind.InvalidValue;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '0')
            {
                offset = i;
                length = 1;
                return Gs1ErrorKind.InvalidValue;
            }
        }
        return Gs1ErrorKind.None;
    }

    /// <summary>RFC 3986 percent-encoding: every <c>%</c> is followed by two hexadecimal digits.</summary>
    private static Gs1ErrorKind Pcenc(ReadOnlySpan<char> value, ref int offset, ref int length)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
                continue;
            if (i + 2 >= value.Length || !IsHex(value[i + 1]) || !IsHex(value[i + 2]))
            {
                offset = i;
                length = Math.Min(3, value.Length - i);
                return Gs1ErrorKind.InvalidValue;
            }
            i += 2;
        }
        return Gs1ErrorKind.None;
    }

    /// <summary>PPTT: a non-zero piece number and an equal-width, non-zero total it does not exceed.</summary>
    private static Gs1ErrorKind Pieceoftotal(ReadOnlySpan<char> value, ref int offset, ref int length)
    {
        if (value.Length == 0 || value.Length % 2 != 0)
            return Gs1ErrorKind.InvalidValue;
        if (!AllDigits(value, out var bad))
        {
            offset = bad;
            length = 1;
            return Gs1ErrorKind.InvalidCharacter;
        }
        var half = value.Length / 2;
        var piece = Number(value.Slice(0, half));
        var total = Number(value.Slice(half));
        if (piece == 0)
        {
            length = half;
            return Gs1ErrorKind.InvalidValue;
        }
        if (total == 0)
        {
            offset = half;
            length = half;
            return Gs1ErrorKind.InvalidValue;
        }
        return piece <= total ? Gs1ErrorKind.None : Gs1ErrorKind.InvalidValue;
    }

    /// <summary><c>pos/end</c>: two numbers without a zero prefix, the position not past the end.</summary>
    private static Gs1ErrorKind Posinseqslash(ReadOnlySpan<char> value, ref int offset, ref int length)
    {
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1 || value.Slice(slash + 1).IndexOf('/') >= 0)
            return Gs1ErrorKind.InvalidValue;
        var position = value.Slice(0, slash);
        var end = value.Slice(slash + 1);
        if (!AllDigits(position, out _) || !AllDigits(end, out _))
            return Gs1ErrorKind.InvalidValue;
        if (position[0] == '0')
        {
            length = slash;
            return Gs1ErrorKind.InvalidValue;
        }
        if (end[0] == '0')
        {
            offset = slash + 1;
            length = end.Length;
            return Gs1ErrorKind.InvalidValue;
        }
        var compare = position.Length != end.Length ? position.Length.CompareTo(end.Length) : position.SequenceCompareTo(end);
        return compare <= 0 ? Gs1ErrorKind.None : Gs1ErrorKind.InvalidValue;
    }

    /// <summary>YYMMDD, with day 00 (the end of the month) where <paramref name="allowDay00"/>. The year resolves through the window around <paramref name="year"/>.</summary>
    private static Gs1ErrorKind Yymmdd(ReadOnlySpan<char> value, int year, bool allowDay00, ref int offset, ref int length)
    {
        if (value.Length != 6)
            return Gs1ErrorKind.InvalidDate;
        if (!AllDigits(value, out var bad))
        {
            offset = bad;
            length = 1;
            return Gs1ErrorKind.InvalidCharacter;
        }
        var yy = Number(value.Slice(0, 2));
        var month = Number(value.Slice(2, 2));
        var day = Number(value.Slice(4, 2));
        return Date(ResolveYear(yy, year), month, day, allowDay00, 2, ref offset, ref length);
    }

    private static Gs1ErrorKind Yyyymmdd(ReadOnlySpan<char> value, ref int offset, ref int length)
    {
        if (value.Length != 8)
            return Gs1ErrorKind.InvalidDate;
        if (!AllDigits(value, out var bad))
        {
            offset = bad;
            length = 1;
            return Gs1ErrorKind.InvalidCharacter;
        }
        var fullYear = Number(value.Slice(0, 4));
        var month = Number(value.Slice(4, 2));
        var day = Number(value.Slice(6, 2));
        return Date(fullYear, month, day, allowDay00: false, 4, ref offset, ref length);
    }

    private static Gs1ErrorKind Date(int fullYear, int month, int day, bool allowDay00, int yearDigits, ref int offset, ref int length)
    {
        if (month < 1 || month > 12)
        {
            offset = yearDigits;
            length = 2;
            return Gs1ErrorKind.InvalidDate;
        }
        var days = month switch
        {
            2 => IsLeap(fullYear) ? 29 : 28,
            4 or 6 or 9 or 11 => 30,
            _ => 31,
        };
        if (day > days || (day == 0 && !allowDay00))
        {
            offset = yearDigits + 2;
            length = 2;
            return Gs1ErrorKind.InvalidDate;
        }
        return Gs1ErrorKind.None;
    }

    /// <summary>General Specifications 7.12: YY is the year within -49 to +50 of the current year.</summary>
    internal static int ResolveYear(int yy, int currentYear)
    {
        var century = currentYear - currentYear % 100;
        var cy = currentYear % 100;
        if (yy - cy >= 51)
            return century - 100 + yy;
        if (yy - cy > -50)
            return century + yy;
        return century + 100 + yy;
    }

    private static bool IsLeap(int fullYear) => fullYear % 4 == 0 && (fullYear % 100 != 0 || fullYear % 400 == 0);

    private static bool IsHex(char c) => Gs1Charsets.IsDigit(c) || (uint)(c - 'A') <= 5 || (uint)(c - 'a') <= 5;

    private static bool AllDigits(ReadOnlySpan<char> value, out int firstNonDigit)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!Gs1Charsets.IsDigit(value[i]))
            {
                firstNonDigit = i;
                return false;
            }
        }
        firstNonDigit = -1;
        return true;
    }

    /// <summary>The value of a short run of digits (at most 9, so an <see cref="int"/> holds it).</summary>
    private static int Number(ReadOnlySpan<char> digits)
    {
        var n = 0;
        foreach (var c in digits)
            n = n * 10 + (c - '0');
        return n;
    }
}
