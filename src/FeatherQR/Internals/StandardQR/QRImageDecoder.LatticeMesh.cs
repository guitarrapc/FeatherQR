using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    /// <summary>
    /// The sampling mesh from version 7: a node at every position of the alignment lattice (grid coordinate c + 0.5 for each Annex E centre coordinate c), each measured on the image around where the finders' frame puts it, moved by what the nodes placed before it were off the frame by.
    /// </summary>
    /// <remarks>
    /// The frame is one plane. A lens or a bend moves the lattice off any plane by more than the search window across a symbol, and by little from one node to the next, so a node's offset from the frame is predicted from its neighbours' and only the difference is searched for.
    /// The order follows from that. First the three corner nodes, which have no pattern: each sits three modules inside its finder's centre, and is placed by that finder's inner outline, its centre and axes measured on the image; the frame's own position stands where the outline is not measured.
    /// Then the patterns on the timing lines, outward from the top-left corner, each with the offset of the node before it on its line, and the interior row by row, each node with the offsets of the three neighbours already placed (left + above − diagonal).
    /// A pattern is searched within four modules of its prediction, and a node not found keeps the prediction.
    /// </remarks>
    /// <returns>True when the mesh should be used: at least half of the interior nodes were found. Below that the nodes are mostly predictions, and the caller keeps the grid it has.</returns>
    internal static bool TryBuildLatticeMesh(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, in FinderFrame frame, int dimension, Span<float> gridCoords, Span<float> nodeXs, Span<float> nodeYs, out int meshSize, out int searchedNodes, out int foundNodes)
    {
        meshSize = 0;
        searchedNodes = 0;
        foundNodes = 0;
        var version = (dimension - 17) / 4;
        // Below version 7 the lattice is the three finders and one pattern, which the four-point transform already anchors on
        if (version < 7)
            return false;

        var baseValues = QRCodeConstants.AlignmentPatternBaseValues.Slice((version - 1) * 7, 7);
        var count = 0;
        for (var i = 0; i < 7; i++)
        {
            if (baseValues[i] != 0)
                gridCoords[count++] = baseValues[i] + 0.5f;
        }
        var last = count - 1;

        Span<float> offsetXs = stackalloc float[MaxMeshNodes * MaxMeshNodes];
        Span<float> offsetYs = stackalloc float[MaxMeshNodes * MaxMeshNodes];
        Span<bool> found = stackalloc bool[MaxMeshNodes * MaxMeshNodes];
        offsetXs.Clear();
        offsetYs.Clear();
        found.Clear();

        var level = grey.EdgeLevel(threshold);
        MeasureCornerNode(luminance, width, height, level, frame, dimension, gridCoords[0], gridCoords[0], frame.TopLeft, 3.5f, 3.5f, ref offsetXs[0], ref offsetYs[0]);
        MeasureCornerNode(luminance, width, height, level, frame, dimension, gridCoords[last], gridCoords[0], frame.TopRight, dimension - 3.5f, 3.5f, ref offsetXs[last], ref offsetYs[last]);
        MeasureCornerNode(luminance, width, height, level, frame, dimension, gridCoords[0], gridCoords[last], frame.BottomLeft, 3.5f, dimension - 3.5f, ref offsetXs[last * count], ref offsetYs[last * count]);

        for (var k = 1; k < last; k++)
        {
            var top = k;
            offsetXs[top] = offsetXs[top - 1];
            offsetYs[top] = offsetYs[top - 1];
            found[top] = TryLocateNode(luminance, width, height, threshold, grey, frame, dimension, gridCoords[k], gridCoords[0], ref offsetXs[top], ref offsetYs[top]);

            var left = k * count;
            offsetXs[left] = offsetXs[left - count];
            offsetYs[left] = offsetYs[left - count];
            found[left] = TryLocateNode(luminance, width, height, threshold, grey, frame, dimension, gridCoords[0], gridCoords[k], ref offsetXs[left], ref offsetYs[left]);
        }

        for (var j = 1; j < count; j++)
        {
            for (var i = 1; i < count; i++)
            {
                var node = j * count + i;
                offsetXs[node] = offsetXs[node - 1] + offsetXs[node - count] - offsetXs[node - count - 1];
                offsetYs[node] = offsetYs[node - 1] + offsetYs[node - count] - offsetYs[node - count - 1];
                found[node] = TryLocateNode(luminance, width, height, threshold, grey, frame, dimension, gridCoords[i], gridCoords[j], ref offsetXs[node], ref offsetYs[node]);
                searchedNodes++;
                if (found[node])
                    foundNodes++;
            }
        }
        if (foundNodes * 2 < searchedNodes)
            return false;

        for (var j = 0; j < count; j++)
        {
            for (var i = 0; i < count; i++)
            {
                var node = j * count + i;
                frame.Map(gridCoords[i], gridCoords[j], dimension, out var x, out var y);
                nodeXs[node] = x + offsetXs[node];
                nodeYs[node] = y + offsetYs[node];
            }
        }
        meshSize = count;
        return true;
    }

    /// <summary>
    /// The offset from the frame of the corner node at grid point (<paramref name="u"/>, <paramref name="v"/>), beside the finder at (<paramref name="finderU"/>, <paramref name="finderV"/>): where that finder's inner outline puts the node, measured from the frame's axes at the finder and again along its own. Left as it is when the outline is not measured.
    /// </summary>
    private static void MeasureCornerNode(ReadOnlySpan<byte> luminance, int width, int height, float level, in FinderFrame frame, int dimension, float u, float v, in FinderPattern finder, float finderU, float finderV, ref float offsetX, ref float offsetY)
    {
        frame.Axes(finderU, finderV, dimension, out var axisX, out var axisY);
        if (!FinderOutline.TryMeasure(luminance, width, height, level, finder.X, finder.Y, axisX.X, axisX.Y, axisY.X, axisY.Y, out var outline))
            return;
        if (outline.TryRefine(luminance, width, height, level, out var refined))
            outline = refined;
        frame.Map(u, v, dimension, out var mappedX, out var mappedY);
        var du = u - finderU;
        var dv = v - finderV;
        offsetX = outline.CenterX + du * outline.UX + dv * outline.VX - mappedX;
        offsetY = outline.CenterY + du * outline.UY + dv * outline.VY - mappedY;
    }

    /// <summary>
    /// Searches the alignment pattern at grid point (<paramref name="u"/>, <paramref name="v"/>) around the frame's position moved by the offset given, and on a find replaces the offset with the measured one.
    /// </summary>
    private static bool TryLocateNode(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, in FinderFrame frame, int dimension, float u, float v, ref float offsetX, ref float offsetY)
    {
        frame.Map(u, v, dimension, out var mappedX, out var mappedY);
        frame.Axes(u, v, dimension, out var axisX, out var axisY);
        var moduleSize = ((float)Math.Sqrt(axisX.X * axisX.X + axisX.Y * axisX.Y) + (float)Math.Sqrt(axisY.X * axisY.X + axisY.Y * axisY.Y)) / 2f;
        var expectedX = mappedX + offsetX;
        var expectedY = mappedY + offsetY;
        if (!TryFindAlignment(luminance, width, height, threshold, grey, expectedX, expectedY, moduleSize, axisX, axisY, 4f, out var x, out var y))
            return false;
        offsetX = x - mappedX;
        offsetY = y - mappedY;
        return true;
    }
}
