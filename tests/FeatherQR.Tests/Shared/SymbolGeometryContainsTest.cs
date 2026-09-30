using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Whether a point lies inside the quadrilateral a symbol's corners bound (<see cref="SymbolGeometry.Contains"/>): the scans
/// skip a later finder candidate inside a symbol that read but did not fit the destination. Inside is strictly inside, either
/// winding, since a mirrored capture's corners run the other way round.
/// </summary>
public class SymbolGeometryContainsTest
{
    private static readonly SymbolCorners Square = Corners(0, 0, 10, 0, 10, 10, 0, 10);
    private static readonly SymbolCorners MirroredSquare = Corners(0, 0, 0, 10, 10, 10, 10, 0);
    private static readonly SymbolCorners Diamond = Corners(5, 0, 10, 5, 5, 10, 0, 5);
    private static readonly SymbolCorners Keystone = Corners(2, 0, 8, 0, 10, 10, 0, 10);

    [Test]
    [Arguments("square", 5f, 5f, true)]
    [Arguments("square", 0.1f, 9.9f, true)]
    [Arguments("mirrored", 5f, 5f, true)]
    [Arguments("diamond", 5f, 5f, true)]
    [Arguments("keystone", 5f, 1f, true)]
    [Arguments("square", 11f, 5f, false)]
    [Arguments("square", -1f, -1f, false)]
    [Arguments("square", 5f, 0f, false)]
    [Arguments("square", 0f, 0f, false)]
    [Arguments("mirrored", 5f, 10.5f, false)]
    [Arguments("diamond", 1f, 1f, false)]
    [Arguments("diamond", 9f, 9f, false)]
    [Arguments("keystone", 0.5f, 1f, false)]
    [Arguments("empty", 0f, 0f, false)]
    public async Task Contains_IsStrictlyInsideTheQuadrilateral_EitherWinding(string shape, float x, float y, bool expected)
    {
        var corners = shape switch
        {
            "square" => Square,
            "mirrored" => MirroredSquare,
            "diamond" => Diamond,
            "keystone" => Keystone,
            _ => default,
        };

        await Assert.That(SymbolGeometry.Contains(corners, x, y)).IsEqualTo(expected);
    }

    private static SymbolCorners Corners(float tlX, float tlY, float trX, float trY, float brX, float brY, float blX, float blY)
        => SymbolGeometry.FromMapped(tlX, tlY, trX, trY, brX, brY, blX, blY, transposed: false);
}
