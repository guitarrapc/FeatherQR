using System.Runtime.CompilerServices;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// int version of Vector2, slim implementation
/// </summary>
/// <remarks>
/// ref: https://github.com/dotnet/corefx/blob/v3.1.32/src/System.Numerics.Vectors/src/System/Numerics/Vector2_Intrinsics.cs
/// </remarks>
public readonly record struct Vector2Slim
{
    /// <summary>
    /// The X component of the vector.
    /// </summary>
    public int X { get; }
    /// <summary>
    /// The Y component of the vector.
    /// </summary>
    public int Y { get; }

    /// <summary>
    /// Creates a vector with both components set to the same value.
    /// </summary>
    /// <param name="value">The value used for both X and Y.</param>
    public Vector2Slim(int value) : this(value, value) { }

    /// <summary>
    /// Creates a vector from its two components.
    /// </summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    public Vector2Slim(int x, int y) => (X, Y) = (x, y);

    /// <summary>
    /// Copies the vector elements to the specified array.
    /// </summary>
    /// <param name="array">The destination array</param>
    /// <exception cref="ArgumentNullException">Thrown when array is null</exception>
    /// <exception cref="ArgumentException">Thrown when array is too short</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopyTo(int[] array)
    {
        if (array is null)
            throw new ArgumentNullException(nameof(array));

        CopyTo(array.AsSpan(), 0);
    }

    /// <summary>
    /// Copies the vector elements to the specified span.
    /// </summary>
    /// <param name="span">The destination span</param>
    /// <exception cref="ArgumentException">Thrown when span is too short</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopyTo(Span<int> span) => CopyTo(span, 0);

    /// <summary>
    /// Copies the vector elements to the specified array starting at the specified index.
    /// </summary>
    /// <param name="array">The destination array</param>
    /// <param name="index">The index at which to begin copying</param>
    /// <exception cref="ArgumentNullException">Thrown when array is null</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when index is out of range</exception>
    /// <exception cref="ArgumentException">Thrown when destination is too short</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopyTo(int[] array, int index)
    {
        if (array is null)
            throw new ArgumentNullException(nameof(array));

        CopyTo(array.AsSpan(), index);
    }

    /// <summary>
    /// Copies the vector elements to the specified span starting at the specified index.
    /// </summary>
    /// <param name="span">The destination span</param>
    /// <param name="index">The index at which to begin copying</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when index is out of range</exception>
    /// <exception cref="ArgumentException">Thrown when destination is too short</exception>
    public void CopyTo(Span<int> span, int index)
    {
        if (span.Length < 2)
            throw new ArgumentException("Destination must have at least 2 elements", nameof(span));
        if (index < 0 || index > span.Length - 2)
            throw new ArgumentOutOfRangeException(nameof(index), $"Index must be between 0 and {span.Length - 2}");

        span[index] = X;
        span[index + 1] = Y;
    }

    /// <summary>
    /// Returns the vector (0,0).
    /// </summary>
    public static readonly Vector2Slim Zero = new(0, 0);

    /// <summary>
    /// Returns the vector (1,1).
    /// </summary>
    public static readonly Vector2Slim One = new(1, 1);

    /// <summary>
    /// Returns the vector (1,0).
    /// </summary>
    public static readonly Vector2Slim UnitX = new(1, 0);
    /// <summary>
    /// Returns the vector (0,1).
    /// </summary>
    public static readonly Vector2Slim UnitY = new(0, 1);
}
