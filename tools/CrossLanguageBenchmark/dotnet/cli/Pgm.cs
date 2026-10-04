using System.Text;

/// <summary>
/// Binary PGM (P5, maxval 255), the one image format of the corpus: every language reads it in a few lines, so no image library enters a CLI.
/// </summary>
internal static class Pgm
{
    public static (byte[] Pixels, int Width, int Height) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var position = 0;
        if (NextToken(bytes, ref position) != "P5")
            throw new InvalidDataException($"{path} is not a binary PGM (P5).");
        var width = int.Parse(NextToken(bytes, ref position));
        var height = int.Parse(NextToken(bytes, ref position));
        var maxValue = int.Parse(NextToken(bytes, ref position));
        if (maxValue != 255)
            throw new InvalidDataException($"{path} has maxval {maxValue}; the corpus uses 255.");

        // Exactly one whitespace byte separates the header from the raster.
        position++;
        if (bytes.Length - position != width * height)
            throw new InvalidDataException($"{path} holds {bytes.Length - position} pixel bytes for {width}x{height}.");
        return (bytes.AsSpan(position).ToArray(), width, height);
    }

    public static void Write(string path, ReadOnlySpan<byte> pixels, int width, int height)
    {
        using var stream = File.Create(path);
        stream.Write(Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n"));
        stream.Write(pixels);
    }

    private static string NextToken(byte[] bytes, ref int position)
    {
        while (position < bytes.Length)
        {
            if (bytes[position] == (byte)'#')
            {
                while (position < bytes.Length && bytes[position] != (byte)'\n')
                    position++;
            }
            else if (IsSpace(bytes[position]))
            {
                position++;
            }
            else
            {
                break;
            }
        }
        var start = position;
        while (position < bytes.Length && !IsSpace(bytes[position]))
            position++;
        return Encoding.ASCII.GetString(bytes, start, position - start);
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';
}
