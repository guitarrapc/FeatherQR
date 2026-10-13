using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using FeatherQR.Gs1.Internals;

namespace FeatherQR.Gs1;

/// <summary>
/// GS1 entry points on <see cref="QRCodeGenerator"/>: <c>CreateGs1</c> writes a GS1 QR Code, the data under FNC1 in
/// first position, and <c>CreateGs1DigitalLink</c> a plain QR Code holding the data's GS1 Digital Link URI.
/// With C# 14 they are reachable as <c>QRCodeGenerator.CreateGs1(...)</c>. On older language versions call them on this class.
/// </summary>
/// <remarks>
/// <para>
/// The text is (AI) text such as <c>(01)09506000134352(17)201225(10)ABC123</c> or an element string, told apart by
/// the first character: (AI) text starts with <c>(</c> and an element string with a digit. Text that starts with
/// anything else, such as a blank or a symbology identifier, is read as an element string and refused. Every check of
/// <see cref="Gs1ElementString.TryValidate"/> runs, pairings included, and data that fails is a
/// <see cref="Gs1FormatException"/> whose <see cref="Gs1FormatException.Error"/> names the place in the text passed.
/// (AI) text gets its separators written where the General Specifications require one. An element string is
/// written as given.
/// </para>
/// <para>
/// Use <c>CreateGs1DigitalLink</c> for retail, where a phone opens the URI and a point of sale reads the GTIN out
/// of it, and <c>CreateGs1</c> elsewhere (healthcare, logistics), where a scanner expects GS1 data under FNC1.
/// Leave <see cref="QRCodeGeneratorOptions.EciMode"/> at its default: GS1 data is ASCII and a GS1 QR Code carries
/// no ECI header. A wrong option is reported as <c>Create</c> reports it, and before the data.
/// </para>
/// </remarks>
public static class Gs1QRCodeGenerator
{
    extension(QRCodeGenerator)
    {
        /// <summary>
        /// Encodes GS1 data into a GS1 QR Code: the element string with FNC1 in first position.
        /// </summary>
        /// <param name="text">(AI) text or an element string. A <see cref="string"/> converts implicitly, except on the netstandard2.0 asset below C# 14, where <c>text.AsSpan()</c> is needed.</param>
        /// <param name="eccLevel">How much damage the QR code should survive.</param>
        /// <param name="options">The settings of <c>Create</c>. Omit for the defaults. <see cref="QRCodeGeneratorOptions.Fnc1"/> is set here, so leave it at its default.</param>
        /// <returns>The module matrix.</returns>
        /// <exception cref="Gs1FormatException">Thrown when <paramref name="text"/> is not valid GS1 data.</exception>
        /// <exception cref="ArgumentException">Thrown when <see cref="QRCodeGeneratorOptions.Fnc1"/> is second position, and as <c>Create</c> throws for its options and for data that does not fit a narrowed version range.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the data does not fit version 40, as <c>Create</c> throws.</exception>
        public static QRCodeData CreateGs1(ReadOnlySpan<char> text, QREccLevel eccLevel, in QRCodeGeneratorOptions options = default)
        {
            var gs1Options = WithFirstPosition(in options);
            char[]? rented = null;
            Span<char> buffer = Gs1SymbolData.StackBufferServes(text) ? stackalloc char[Gs1SymbolData.StackLimit] : (rented = ArrayPool<char>.Shared.Rent(text.Length));
            try
            {
                if (!Gs1SymbolData.TryGetElementString(text, DateTime.UtcNow.Year, buffer, out var elementString, out var error))
                    ThrowForData(error, eccLevel, in gs1Options);
                return QRCodeGenerator.Create(elementString, eccLevel, in gs1Options);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<char>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// Encodes GS1 data into a GS1 QR Code in the buffer you provide, without allocating.
        /// </summary>
        /// <param name="text">(AI) text or an element string.</param>
        /// <param name="eccLevel">How much damage the QR code should survive.</param>
        /// <param name="destination">Where to write the matrix, one byte a module as <c>Create</c> writes it. Size it with <see cref="TryGetRequiredBufferSizeGs1"/>.</param>
        /// <param name="options">The settings of <c>Create</c>. Size <paramref name="destination"/> with the same options.</param>
        /// <returns>The number of bytes written.</returns>
        /// <exception cref="Gs1FormatException">Thrown when <paramref name="text"/> is not valid GS1 data.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too small, when <see cref="QRCodeGeneratorOptions.Fnc1"/> is second position, and as <c>Create</c> throws for its options and for data that does not fit a narrowed version range.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the data does not fit version 40, as <c>Create</c> throws.</exception>
        public static int CreateGs1(ReadOnlySpan<char> text, QREccLevel eccLevel, Span<byte> destination, in QRCodeGeneratorOptions options = default)
        {
            var gs1Options = WithFirstPosition(in options);
            char[]? rented = null;
            Span<char> buffer = Gs1SymbolData.StackBufferServes(text) ? stackalloc char[Gs1SymbolData.StackLimit] : (rented = ArrayPool<char>.Shared.Rent(text.Length));
            try
            {
                if (!Gs1SymbolData.TryGetElementString(text, DateTime.UtcNow.Year, buffer, out var elementString, out var error))
                    ThrowForData(error, eccLevel, in gs1Options);
                try
                {
                    return QRCodeGenerator.Create(elementString, eccLevel, destination, in gs1Options);
                }
                catch (ArgumentException shortDestination) when (shortDestination.ParamName == nameof(destination))
                {
                    QRCodeGenerator.TryGetRequiredBufferSize(elementString, eccLevel, out var required, in gs1Options);
                    throw DestinationTooSmall(required.BufferSize, destination.Length, nameof(Gs1QRCodeGenerator.TryGetRequiredBufferSizeGs1), shortDestination);
                }
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<char>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// Reports how large the GS1 QR Code of the data will be, or <c>false</c> when the data is not valid GS1 data or does not fit.
        /// </summary>
        /// <param name="text">(AI) text or an element string.</param>
        /// <param name="eccLevel">How much damage the QR code should survive.</param>
        /// <param name="size">The size on success, <c>default</c> otherwise.</param>
        /// <param name="options">The settings of <c>Create</c>. Pass the options you will encode with.</param>
        /// <returns><c>true</c> when the data is valid and fits. To learn why not, check (AI) text with <see cref="Gs1ElementString.TryFromHri(ReadOnlySpan{char}, out string, out Gs1Error)"/> and an element string with <see cref="Gs1ElementString.TryValidate"/>. Data that passes is data that does not fit.</returns>
        /// <exception cref="ArgumentException">Thrown when <see cref="QRCodeGeneratorOptions.Fnc1"/> is second position, and as <c>TryGetRequiredBufferSize</c> throws for its options.</exception>
        public static bool TryGetRequiredBufferSizeGs1(ReadOnlySpan<char> text, QREccLevel eccLevel, out QRCodeCalculatedSize size, in QRCodeGeneratorOptions options = default)
        {
            size = default;
            var gs1Options = WithFirstPosition(in options);
            if (Gs1SymbolData.IsPastCapacity(text, Gs1SymbolData.StandardQrCapacity))
            {
                CheckOptions(eccLevel, in gs1Options);
                return false;
            }

            char[]? rented = null;
            Span<char> buffer = Gs1SymbolData.StackBufferServes(text) ? stackalloc char[Gs1SymbolData.StackLimit] : (rented = ArrayPool<char>.Shared.Rent(text.Length));
            try
            {
                if (!Gs1SymbolData.TryGetElementString(text, DateTime.UtcNow.Year, buffer, out var elementString, out _))
                {
                    CheckOptions(eccLevel, in gs1Options);
                    return false;
                }
                return QRCodeGenerator.TryGetRequiredBufferSize(elementString, eccLevel, out size, in gs1Options);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<char>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// Encodes the GS1 Digital Link URI of GS1 data into a QR Code, the carrier retail uses: plain text, no FNC1.
        /// </summary>
        /// <remarks>
        /// The URI is the one <see cref="Gs1ElementString.TryToDigitalLink(ReadOnlySpan{char}, out string, out Gs1Error, string?)"/> builds: the primary key and its qualifiers in the path, the other AIs as query attributes.
        /// Data with no primary key, or with an AI a URI cannot carry, is refused.
        /// </remarks>
        /// <param name="text">(AI) text or an element string.</param>
        /// <param name="eccLevel">How much damage the QR code should survive.</param>
        /// <param name="options">The settings of <c>Create</c>. Omit for the defaults. <see cref="QRCodeGeneratorOptions.Fnc1"/> must stay at its default.</param>
        /// <param name="stem">The URI's start, an <c>http</c> or <c>https</c> prefix with a host such as <c>https://brand.example.com</c>. <c>null</c> for the canonical <c>https://id.gs1.org</c>.</param>
        /// <returns>The module matrix.</returns>
        /// <exception cref="Gs1FormatException">Thrown when <paramref name="text"/> is not valid GS1 data or is data a Digital Link URI cannot carry.</exception>
        /// <exception cref="ArgumentException">Thrown when <see cref="QRCodeGeneratorOptions.Fnc1"/> is set, when <paramref name="stem"/> is not such a prefix, and as <c>Create</c> throws for its options and for a URI that does not fit a narrowed version range.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the URI does not fit version 40, as <c>Create</c> throws.</exception>
        public static QRCodeData CreateGs1DigitalLink(ReadOnlySpan<char> text, QREccLevel eccLevel, in QRCodeGeneratorOptions options = default, string? stem = null)
        {
            RequireNoFnc1(in options);
            var stemText = Gs1DigitalLink.ValidateStem(stem);
            var bound = Gs1SymbolData.DigitalLinkBound(text.Length, stemText.Length);
            char[]? rentedElement = null;
            char[]? rentedUri = null;
            Span<char> elementBuffer = Gs1SymbolData.StackBufferServes(text) ? stackalloc char[Gs1SymbolData.StackLimit] : (rentedElement = ArrayPool<char>.Shared.Rent(text.Length));
            Span<char> uriBuffer = bound <= Gs1SymbolData.StackLimit ? stackalloc char[Gs1SymbolData.StackLimit] : (rentedUri = ArrayPool<char>.Shared.Rent(bound));
            try
            {
                if (!Gs1SymbolData.TryGetDigitalLink(text, DateTime.UtcNow.Year, stemText, elementBuffer, uriBuffer, out var uri, out var error))
                    ThrowForData(error, eccLevel, in options);
                return QRCodeGenerator.Create(uri, eccLevel, in options);
            }
            finally
            {
                if (rentedElement is not null)
                    ArrayPool<char>.Shared.Return(rentedElement);
                if (rentedUri is not null)
                    ArrayPool<char>.Shared.Return(rentedUri);
            }
        }

        /// <summary>
        /// Encodes the GS1 Digital Link URI of GS1 data into a QR Code under the canonical stem, with the options you pass.
        /// </summary>
        /// <remarks>
        /// The form a bare <c>default</c> or a target-typed <c>new()</c> selects as the third argument. Without it such an argument would select the form that takes a destination.
        /// </remarks>
        /// <param name="text">(AI) text or an element string.</param>
        /// <param name="eccLevel">How much damage the QR code should survive.</param>
        /// <param name="options">The settings of <c>Create</c>. <see cref="QRCodeGeneratorOptions.Fnc1"/> must stay at its default.</param>
        /// <returns>The module matrix.</returns>
        /// <exception cref="Gs1FormatException">Thrown when <paramref name="text"/> is not valid GS1 data or is data a Digital Link URI cannot carry.</exception>
        /// <exception cref="ArgumentException">Thrown when <see cref="QRCodeGeneratorOptions.Fnc1"/> is set, and as <c>Create</c> throws for its options and for a URI that does not fit a narrowed version range.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the URI does not fit version 40, as <c>Create</c> throws.</exception>
        public static QRCodeData CreateGs1DigitalLink(ReadOnlySpan<char> text, QREccLevel eccLevel, in QRCodeGeneratorOptions options)
            => QRCodeGenerator.CreateGs1DigitalLink(text, eccLevel, in options, null);

        /// <summary>
        /// Encodes the GS1 Digital Link URI of GS1 data into a QR Code in the buffer you provide, without allocating.
        /// </summary>
        /// <param name="text">(AI) text or an element string.</param>
        /// <param name="eccLevel">How much damage the QR code should survive.</param>
        /// <param name="destination">Where to write the matrix, one byte a module as <c>Create</c> writes it. Size it with <see cref="TryGetRequiredBufferSizeGs1DigitalLink"/>.</param>
        /// <param name="options">The settings of <c>Create</c>. Size <paramref name="destination"/> with the same options and stem.</param>
        /// <param name="stem">The URI's start. <c>null</c> for the canonical <c>https://id.gs1.org</c>.</param>
        /// <returns>The number of bytes written.</returns>
        /// <exception cref="Gs1FormatException">Thrown when <paramref name="text"/> is not valid GS1 data or is data a Digital Link URI cannot carry.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too small, when <see cref="QRCodeGeneratorOptions.Fnc1"/> is set, when <paramref name="stem"/> is not an <c>http</c> or <c>https</c> prefix with a host, and as <c>Create</c> throws for its options and for a URI that does not fit a narrowed version range.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the URI does not fit version 40, as <c>Create</c> throws.</exception>
        public static int CreateGs1DigitalLink(ReadOnlySpan<char> text, QREccLevel eccLevel, Span<byte> destination, in QRCodeGeneratorOptions options = default, string? stem = null)
        {
            RequireNoFnc1(in options);
            var stemText = Gs1DigitalLink.ValidateStem(stem);
            var bound = Gs1SymbolData.DigitalLinkBound(text.Length, stemText.Length);
            char[]? rentedElement = null;
            char[]? rentedUri = null;
            Span<char> elementBuffer = Gs1SymbolData.StackBufferServes(text) ? stackalloc char[Gs1SymbolData.StackLimit] : (rentedElement = ArrayPool<char>.Shared.Rent(text.Length));
            Span<char> uriBuffer = bound <= Gs1SymbolData.StackLimit ? stackalloc char[Gs1SymbolData.StackLimit] : (rentedUri = ArrayPool<char>.Shared.Rent(bound));
            try
            {
                if (!Gs1SymbolData.TryGetDigitalLink(text, DateTime.UtcNow.Year, stemText, elementBuffer, uriBuffer, out var uri, out var error))
                    ThrowForData(error, eccLevel, in options);
                try
                {
                    return QRCodeGenerator.Create(uri, eccLevel, destination, in options);
                }
                catch (ArgumentException shortDestination) when (shortDestination.ParamName == nameof(destination))
                {
                    QRCodeGenerator.TryGetRequiredBufferSize(uri, eccLevel, out var required, in options);
                    throw DestinationTooSmall(required.BufferSize, destination.Length, nameof(Gs1QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink), shortDestination);
                }
            }
            finally
            {
                if (rentedElement is not null)
                    ArrayPool<char>.Shared.Return(rentedElement);
                if (rentedUri is not null)
                    ArrayPool<char>.Shared.Return(rentedUri);
            }
        }

        /// <summary>
        /// Reports how large the QR Code of the data's Digital Link URI will be, or <c>false</c> when the data is not valid GS1 data, is data a URI cannot carry, or does not fit.
        /// </summary>
        /// <param name="text">(AI) text or an element string.</param>
        /// <param name="eccLevel">How much damage the QR code should survive.</param>
        /// <param name="size">The size on success, <c>default</c> otherwise.</param>
        /// <param name="options">The settings of <c>Create</c>. Pass the options you will encode with.</param>
        /// <param name="stem">The URI's start. <c>null</c> for the canonical <c>https://id.gs1.org</c>.</param>
        /// <returns><c>true</c> when the data is valid, a URI can carry it and it fits. To learn why not, pass an element string to <see cref="Gs1ElementString.TryToDigitalLink(ReadOnlySpan{char}, out string, out Gs1Error, string?)"/>, and (AI) text to <see cref="Gs1ElementString.TryFromHri(ReadOnlySpan{char}, out string, out Gs1Error)"/> and then its element string to that member. Data that passes is data that does not fit.</returns>
        /// <exception cref="ArgumentException">Thrown when <see cref="QRCodeGeneratorOptions.Fnc1"/> is set, when <paramref name="stem"/> is not an <c>http</c> or <c>https</c> prefix with a host, and as <c>TryGetRequiredBufferSize</c> throws for its options.</exception>
        public static bool TryGetRequiredBufferSizeGs1DigitalLink(ReadOnlySpan<char> text, QREccLevel eccLevel, out QRCodeCalculatedSize size, in QRCodeGeneratorOptions options = default, string? stem = null)
        {
            size = default;
            RequireNoFnc1(in options);
            var stemText = Gs1DigitalLink.ValidateStem(stem);
            var bound = Gs1SymbolData.DigitalLinkBound(text.Length, stemText.Length);
            char[]? rentedElement = null;
            char[]? rentedUri = null;
            Span<char> elementBuffer = Gs1SymbolData.StackBufferServes(text) ? stackalloc char[Gs1SymbolData.StackLimit] : (rentedElement = ArrayPool<char>.Shared.Rent(text.Length));
            Span<char> uriBuffer = bound <= Gs1SymbolData.StackLimit ? stackalloc char[Gs1SymbolData.StackLimit] : (rentedUri = ArrayPool<char>.Shared.Rent(bound));
            try
            {
                if (!Gs1SymbolData.TryGetDigitalLink(text, DateTime.UtcNow.Year, stemText, elementBuffer, uriBuffer, out var uri, out _))
                {
                    CheckOptions(eccLevel, in options);
                    return false;
                }
                return QRCodeGenerator.TryGetRequiredBufferSize(uri, eccLevel, out size, in options);
            }
            finally
            {
                if (rentedElement is not null)
                    ArrayPool<char>.Shared.Return(rentedElement);
                if (rentedUri is not null)
                    ArrayPool<char>.Shared.Return(rentedUri);
            }
        }
    }

    /// <summary>The caller's options with FNC1 in first position, which is what makes the symbol a GS1 QR Code. Second position is another mode, so asking for it here is a contradiction.</summary>
    private static QRCodeGeneratorOptions WithFirstPosition(in QRCodeGeneratorOptions options)
    {
        if (options.Fnc1.IsSecondPosition)
            throw new ArgumentException("CreateGs1 writes FNC1 in first position, the GS1 mode. Leave Fnc1 at its default, or use Create for a second-position symbol.", nameof(options));

        var gs1Options = options;
        gs1Options.Fnc1 = Fnc1Mode.FirstPosition;
        return gs1Options;
    }

    /// <summary>A Digital Link URI is plain text. Under FNC1 a scanner would read the URI as an element string.</summary>
    private static void RequireNoFnc1(in QRCodeGeneratorOptions options)
    {
        if (options.Fnc1 != Fnc1Mode.None)
            throw new ArgumentException("A GS1 Digital Link URI is written without FNC1. Leave Fnc1 at its default, or use CreateGs1 for a GS1 QR Code.", nameof(options));
    }

    /// <summary>
    /// Create reports a wrong option before content that does not fit, and the entry points keep that order for
    /// data that is not GS1 data. Sizing a one-digit text runs Create's checks of the options, which come before
    /// the fit, and the answer is not used. This runs on the failing path alone, since on the other one Create
    /// checks the options itself.
    /// </summary>
    private static void CheckOptions(QREccLevel eccLevel, in QRCodeGeneratorOptions options)
        => QRCodeGenerator.TryGetRequiredBufferSize("0".AsSpan(), eccLevel, out _, in options);

    /// <summary>
    /// The error for a destination shorter than the symbol. Create's own message names Create's sizing member,
    /// which sizes the caller's text as it stands and so answers for another symbol than the one written here.
    /// </summary>
    private static ArgumentException DestinationTooSmall(int required, int actual, string sizingMember, ArgumentException inner)
        => new($"Destination buffer too small: {required} bytes required, got {actual} bytes. Use {sizingMember} to calculate the required size.", "destination", inner);

    [DoesNotReturn]
    private static void ThrowForData(Gs1Error error, QREccLevel eccLevel, in QRCodeGeneratorOptions options)
    {
        CheckOptions(eccLevel, in options);
        throw new Gs1FormatException(error);
    }
}
