using System.Runtime.CompilerServices;

namespace FeatherQR.Gs1.Internals;

/// <summary>
/// The code lists the dictionary's linters need and the dictionary file does not hold: ISO 3166-1 numeric
/// and alpha-2 country codes, ISO 4217 numeric currency codes, the AIDC media types of AI (7241) and the
/// package types of AI (7041). Generated from the reference linters' sources committed beside the dictionary
/// (<c>Gs1CodeLists.Generated.cs</c>), as bit fields indexed by code and a sorted list of package types.
/// </summary>
internal static partial class Gs1CodeLists
{
    private const int PackageTypeWidth = 4;

    /// <summary>ISO 3166-1 numeric, 000 to 999.</summary>
    public static bool IsIso3166Numeric(int code) => Bit(Iso3166Numeric, code, 1000);

    /// <summary>ISO 3166-1 alpha-2, two upper-case letters.</summary>
    public static bool IsIso3166Alpha2(char first, char second)
    {
        if ((uint)(first - 'A') > 25 || (uint)(second - 'A') > 25)
            return false;
        return Bit(Iso3166Alpha2, (first - 'A') * 26 + (second - 'A'), 26 * 26);
    }

    /// <summary>ISO 4217 numeric, 000 to 999.</summary>
    public static bool IsIso4217(int code) => Bit(Iso4217, code, 1000);

    /// <summary>The AIDC media types of AI (7241), 00 to 99.</summary>
    public static bool IsMediaType(int code) => Bit(MediaTypes, code, 100);

    /// <summary>The package type codes of AI (7041), 1 to 4 characters, as the reference linter lists them.</summary>
    public static bool IsPackageType(ReadOnlySpan<char> code)
    {
        if (code.Length == 0 || code.Length > PackageTypeWidth)
            return false;
        var low = 0;
        var high = PackageTypeCount - 1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            var comparison = Compare(code, PackageTypes.Slice(middle * PackageTypeWidth, PackageTypeWidth));
            if (comparison == 0)
                return true;
            if (comparison < 0)
                high = middle - 1;
            else
                low = middle + 1;
        }
        return false;
    }

    /// <summary>Ordinal comparison of a code against a zero-padded table row.</summary>
    private static int Compare(ReadOnlySpan<char> code, ReadOnlySpan<byte> row)
    {
        for (var i = 0; i < PackageTypeWidth; i++)
        {
            var expected = row[i];
            var actual = i < code.Length ? code[i] : 0;
            if (actual != expected)
                return actual - expected;
        }
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Bit(ReadOnlySpan<byte> field, int index, int count)
    {
        if ((uint)index >= (uint)count)
            return false;
        return (field[index >> 3] & (1 << (index & 7))) != 0;
    }
}
