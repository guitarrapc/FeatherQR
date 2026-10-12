using System.Buffers;

namespace FeatherQR.Gs1.Internals;

/// <summary>
/// The decode-side read: a decoder's text with its <see cref="Fnc1Mode"/> to (AI) text. Under FNC1 in first
/// position the text is an element string and formats as <see cref="Gs1Hri"/> does. Otherwise it is read as a
/// Digital Link URI into a scratch element string, which then formats the same way. The URI reader guarantees an
/// element string that splits back into the URI's elements, so the second step cannot fail on what the first
/// accepted; should it, the error names the whole text.
/// </summary>
internal static class Gs1Reader
{
    private const int StackLimit = 512;

    public static bool TryRead(ReadOnlySpan<char> text, Fnc1Mode fnc1, out string hri, out Gs1Error error)
    {
        if (fnc1.IsFirstPosition)
            return Gs1Hri.TryToHri(text, out hri, out error);

        hri = "";
        if (text.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.NotAUri, 0, 0);
            return false;
        }

        char[]? rented = null;
        Span<char> buffer = text.Length <= StackLimit ? stackalloc char[StackLimit] : (rented = ArrayPool<char>.Shared.Rent(text.Length));
        var read = Gs1DigitalLink.Read(text, buffer, out var length, out error) && Gs1Hri.TryToHri(buffer.Slice(0, length), out hri, out error);
        if (rented is not null)
            ArrayPool<char>.Shared.Return(rented);
        if (!read)
            error = Clamp(error, text.Length);
        return read;
    }

    public static bool TryRead(ReadOnlySpan<char> text, Fnc1Mode fnc1, Span<char> destination, out int charsWritten, out Gs1Error error)
    {
        if (fnc1.IsFirstPosition)
            return Gs1Hri.TryToHri(text, destination, out charsWritten, out error);

        charsWritten = 0;
        if (text.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.NotAUri, 0, 0);
            return false;
        }

        char[]? rented = null;
        Span<char> buffer = text.Length <= StackLimit ? stackalloc char[StackLimit] : (rented = ArrayPool<char>.Shared.Rent(text.Length));
        var read = Gs1DigitalLink.Read(text, buffer, out var length, out error) && Gs1Hri.TryToHri(buffer.Slice(0, length), destination, out charsWritten, out error);
        if (rented is not null)
            ArrayPool<char>.Shared.Return(rented);
        if (!read)
            error = Clamp(error, text.Length);
        return read;
    }

    public static bool TryGetReadLength(ReadOnlySpan<char> text, Fnc1Mode fnc1, out int length, out Gs1Error error)
    {
        if (fnc1.IsFirstPosition)
            return Gs1Hri.TryGetToHriLength(text, out length, out error);

        length = 0;
        if (text.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.NotAUri, 0, 0);
            return false;
        }

        char[]? rented = null;
        Span<char> buffer = text.Length <= StackLimit ? stackalloc char[StackLimit] : (rented = ArrayPool<char>.Shared.Rent(text.Length));
        var read = Gs1DigitalLink.Read(text, buffer, out var elementStringLength, out error) && Gs1Hri.TryGetToHriLength(buffer.Slice(0, elementStringLength), out length, out error);
        if (rented is not null)
            ArrayPool<char>.Shared.Return(rented);
        if (!read)
            error = Clamp(error, text.Length);
        return read;
    }

    /// <summary>An error from the formatting step names the element string, not the text, so it is widened to the whole text. Errors from the URI reader already point into the text.</summary>
    private static Gs1Error Clamp(Gs1Error error, int textLength)
        => error.Kind == Gs1ErrorKind.DestinationTooSmall || error.Offset + error.Length <= textLength ? error : new Gs1Error(error.Kind, 0, textLength);
}
