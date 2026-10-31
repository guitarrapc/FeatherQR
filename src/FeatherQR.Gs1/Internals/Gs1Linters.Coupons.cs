namespace FeatherQR.Gs1.Internals;

/// <summary>The two coupon formats: the North American Coupon Code of AI (8110) and the paperless coupon code of AI (8112), as fields of variable length each announced by a one-digit length indicator (VLI).</summary>
internal static partial class Gs1Linters
{
    /// <summary>A cursor over the coupon's digits that reports the first field it cannot read.</summary>
    private ref struct CouponReader
    {
        private readonly ReadOnlySpan<char> _value;
        public int Position;
        public bool Failed;
        public int FailOffset;
        public int FailLength;

        public CouponReader(ReadOnlySpan<char> value)
        {
            _value = value;
            Position = 0;
            Failed = false;
            FailOffset = 0;
            FailLength = 0;
        }

        public readonly bool AtEnd => Position >= _value.Length;

        /// <summary>One digit, or a failure at the current position.</summary>
        public int Digit()
        {
            if (Failed)
                return -1;
            if (Position >= _value.Length || !Gs1Charsets.IsDigit(_value[Position]))
            {
                Fail(Position, Math.Min(1, _value.Length - Position));
                return -1;
            }
            return _value[Position++] - '0';
        }

        /// <summary>A digit that must be one of the allowed values, given as a bit per digit.</summary>
        public int DigitIn(int allowedMask)
        {
            var start = Position;
            var d = Digit();
            if (d >= 0 && (allowedMask & (1 << d)) == 0)
            {
                Fail(start, 1);
                return -1;
            }
            return d;
        }

        /// <summary>Takes <paramref name="count"/> digits, failing where fewer remain or one is not a digit.</summary>
        public ReadOnlySpan<char> Digits(int count)
        {
            if (Failed)
                return default;
            var start = Position;
            if (_value.Length - Position < count)
            {
                Fail(Position, _value.Length - Position);
                return default;
            }
            for (var i = 0; i < count; i++)
            {
                if (!Gs1Charsets.IsDigit(_value[Position + i]))
                {
                    Fail(Position + i, 1);
                    return default;
                }
            }
            Position += count;
            return _value.Slice(start, count);
        }

        public void Fail(int offset, int length)
        {
            if (Failed)
                return;
            Failed = true;
            FailOffset = offset;
            FailLength = Math.Max(0, length);
        }
    }

    private const int Digits0To4Or9 = 0b10_0001_1111;
    private const int Digits0To3 = 0b1111;
    private const int Digits0To6 = 0b111_1111;
    private const int Digits1To5 = 0b11_1110;
    private const int Digits1To7 = 0b1111_1110;
    private const int Digits0To1 = 0b11;
    private const int Digits0To2 = 0b111;
    private const int Digits0To1Or2Or5Or6 = 0b110_0111;
    private const int Digits0To6Or9 = 0b10_0111_1111;

    /// <summary>
    /// Validates AI (8110), the North American Coupon Code.
    /// </summary>
    private static Gs1ErrorKind Couponcode(ReadOnlySpan<char> value, int year, ref int offset, ref int length)
    {
        // Required fields: GS1 Company Prefix (VLI 0-6 gives 6-12 digits), six-digit offer code,
        // save value (VLI 1-5), and purchase requirement (VLI 1-5, code 0-4 or 9, three-digit family).
        // Optional fields must appear in ascending ID order:
        // 1: additional purchase rules (0-3), second requirement and GCP (VLI 0-6, or 9 to reuse the primary).
        // 2: third purchase requirement and GCP.
        // 3/4: expiration/start dates (YYMMDD), with start no later than expiration.
        // 5: serial number (VLI 0-9 gives 6-15 digits).
        // 6: retailer prefix or GLN (VLI 1-7 gives 7-13 digits).
        // 9: save-value code (0,1,2,5,6), applicability (0-2), store-coupon and do-not-multiply flags (0/1).
        var reader = new CouponReader(value);
        var gcpLength = reader.DigitIn(Digits0To6);
        if (gcpLength >= 0)
            reader.Digits(6 + gcpLength);
        reader.Digits(6);
        var saveLength = reader.DigitIn(Digits1To5);
        if (saveLength >= 0)
            reader.Digits(saveLength);
        PurchaseRequirement(ref reader);

        var lastField = 0;
        var expiration = -1;
        var start = -1;
        while (!reader.Failed && !reader.AtEnd)
        {
            var fieldStart = reader.Position;
            var field = reader.Digit();
            if (field < 0)
                break;
            if (field <= lastField || field is 0 or 7 or 8)
            {
                reader.Fail(fieldStart, 1);
                break;
            }
            lastField = field;
            switch (field)
            {
                case 1:
                    reader.DigitIn(Digits0To3);
                    PurchaseRequirement(ref reader);
                    SecondaryGcp(ref reader);
                    break;
                case 2:
                    PurchaseRequirement(ref reader);
                    SecondaryGcp(ref reader);
                    break;
                case 3:
                    expiration = CouponDate(ref reader, year);
                    break;
                case 4:
                    start = CouponDate(ref reader, year);
                    if (expiration >= 0 && start >= 0 && expiration < start)
                        reader.Fail(fieldStart + 1, 6);
                    break;
                case 5:
                    var serialLength = reader.Digit();
                    if (serialLength >= 0)
                        reader.Digits(6 + serialLength);
                    break;
                case 6:
                    var retailerLength = reader.DigitIn(Digits1To7);
                    if (retailerLength >= 0)
                        reader.Digits(6 + retailerLength);
                    break;
                case 9:
                    reader.DigitIn(Digits0To1Or2Or5Or6);
                    reader.DigitIn(Digits0To2);
                    reader.Digit();
                    reader.DigitIn(Digits0To1);
                    break;
            }
        }

        return Result(ref reader, ref offset, ref length);
    }

    private static void PurchaseRequirement(ref CouponReader reader)
    {
        var requirementLength = reader.DigitIn(Digits1To5);
        if (requirementLength >= 0)
            reader.Digits(requirementLength);
        reader.DigitIn(Digits0To4Or9);
        reader.Digits(3);
    }

    private static void SecondaryGcp(ref CouponReader reader)
    {
        var gcpLength = reader.DigitIn(Digits0To6Or9);
        if (gcpLength >= 0 && gcpLength != 9)
            reader.Digits(6 + gcpLength);
    }

    /// <summary>
    /// A YYMMDD date field, as the number its six digits spell, or -1 when it is not a date. The reference linter
    /// orders the expiry and start dates by comparing the six characters, not through the two-digit year window,
    /// and the number orders the same way.
    /// </summary>
    private static int CouponDate(ref CouponReader reader, int year)
    {
        var start = reader.Position;
        var digits = reader.Digits(6);
        if (reader.Failed)
            return -1;
        var dateOffset = 0;
        var dateLength = 6;
        if (Yymmdd(digits, year, allowDay00: false, ref dateOffset, ref dateLength) != Gs1ErrorKind.None)
        {
            reader.Fail(start + dateOffset, dateLength);
            return -1;
        }
        return Number(digits);
    }

    /// <summary>
    /// AI (8112), the paperless coupon code: a format code (0 or 1), a funder identifier (VLI 0-6 for 6 to 12
    /// digits), a 6-digit offer code and a serial number (VLI 0-9 for 6 to 15 digits), and nothing after it.
    /// </summary>
    private static Gs1ErrorKind Couponposoffer(ReadOnlySpan<char> value, ref int offset, ref int length)
    {
        var reader = new CouponReader(value);
        reader.DigitIn(Digits0To1);
        var funderLength = reader.DigitIn(Digits0To6);
        if (funderLength >= 0)
            reader.Digits(6 + funderLength);
        reader.Digits(6);
        var serialLength = reader.Digit();
        if (serialLength >= 0)
            reader.Digits(6 + serialLength);
        if (!reader.Failed && !reader.AtEnd)
            reader.Fail(reader.Position, value.Length - reader.Position);
        return Result(ref reader, ref offset, ref length);
    }

    private static Gs1ErrorKind Result(ref CouponReader reader, ref int offset, ref int length)
    {
        if (!reader.Failed)
            return Gs1ErrorKind.None;
        offset = reader.FailOffset;
        length = reader.FailLength;
        return Gs1ErrorKind.InvalidValue;
    }
}
