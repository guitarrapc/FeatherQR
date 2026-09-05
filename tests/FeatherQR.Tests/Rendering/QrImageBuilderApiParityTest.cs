using System.Reflection;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// Guards the intended 1:1 correspondence between the symbology-specific image
/// builders: every public member of <see cref="QRCodeImageBuilder"/> must exist on
/// <see cref="MicroQRCodeImageBuilder"/> and <see cref="RmQRCodeImageBuilder"/> with
/// the symbology types swapped (QRCodeData ⇔ MicroQRCodeData ⇔ RmQRCodeData,
/// QREccLevel ⇔ MicroQREccLevel ⇔ RmQREccLevel, int version ⇔ MicroQRVersion ⇔
/// RmQRVersion), and vice versa, except for the documented per-symbology options.
/// Adding an output method or static helper to one builder without the others
/// fails here.
/// </summary>
public class QrImageBuilderApiParityTest
{
    /// <summary>
    /// Standard QR-only fluent options: Micro QR and rMQR have a single finder
    /// pattern and no ECC headroom for overlays. ECC boost is Standard QR-only for
    /// related reasons: Micro QR ties the legal ECC levels to the version (M1 has
    /// none, M4 alone offers Q), and rMQR's two levels leave a single M→H step that
    /// has not been asked for; both can join later without changing the contract.
    /// </summary>
    private static readonly string[] standardOnlyMembers =
    [
        "WithIcon",
        "WithFinderPatternShape",
        "WithErrorCorrectionBoost",
    ];

    /// <summary>
    /// rMQR-only fluent options: version fit is two-dimensional (design record
    /// specs/rmqr-encoder.md), so the rMQR builder alone exposes the fit strategy and
    /// the fixed-height constraint; and the symbol is rectangular, so the width-only
    /// sizing rule (height from the aspect ratio) is a builder option of its own.
    /// </summary>
    private static readonly string[] rmqrOnlyMembers =
    [
        "WithFitStrategy",
        "WithHeight",
        "WithWidth",
    ];

    // WithSegmentation is shared by all three builders (each with its own
    // segmentation enum, canonicalized to SEGMENTATION below), so no per-symbology
    // exclusion list is needed for it any more.

    /// <summary>
    /// Options the two symbologies with a totally ordered version set have and rMQR does
    /// not. rMQR's 32 versions have no min/max relation (R7x43, R9x43 and R7x59 cannot be
    /// ordered), so a version range has no meaning there; it constrains its fit with
    /// <c>WithFitStrategy</c> and <c>WithHeight</c> instead.
    /// </summary>
    /// <remarks>
    /// Matched on the whole normalized signature rather than the member name, unlike the
    /// lists above: <c>WithVersion</c> itself is shared by all three builders and only its
    /// range-taking overload is absent from rMQR.
    /// </remarks>
    private static readonly string[] orderedVersionOnlySignatures =
    [
        " WithVersion(VERSIONRANGE)",
    ];

    /// <summary>
    /// Options Standard QR and Micro QR share and rMQR does not. Mask pinning: both
    /// symbologies select among several data mask patterns (eight for Standard QR,
    /// four for Micro QR, an <c>int?</c> in both builders), while rMQR has a single
    /// fixed mask (ISO/IEC 23941), so there is nothing to pin.
    /// </summary>
    private static readonly string[] standardAndMicroOnlySignatures =
    [
        " WithMaskPattern(",
    ];

    [Test]
    public async Task PublicSurface_CorrespondsOneToOne_ModuloDocumentedDifferences()
    {
        var standard = NormalizedSignatures(typeof(QRCodeImageBuilder))
            .Where(s => !standardOnlyMembers.Any(m => s.Contains($" {m}(")))
            .ToHashSet();
        var micro = NormalizedSignatures(typeof(MicroQRCodeImageBuilder)).ToHashSet();
        var rmqr = NormalizedSignatures(typeof(RmQRCodeImageBuilder))
            .Where(s => !rmqrOnlyMembers.Any(m => s.Contains($" {m}(")))
            .ToHashSet();

        var failures = new List<string>();
        Compare("MicroQRCodeImageBuilder", micro, standard.Where(s => !s.Contains(" WithEciMode(")));
        Compare("RmQRCodeImageBuilder", rmqr, standard.Where(s => !orderedVersionOnlySignatures.Any(s.Contains) && !standardAndMicroOnlySignatures.Any(s.Contains)));
        if (failures.Count > 0)
            Assert.Fail("Image builder surfaces drifted apart.\n" + string.Join("\n", failures));

        // Sanity: the normalization must leave a substantial shared surface
        await Assert.That(standard.Count).IsGreaterThan(20);

        void Compare(string name, HashSet<string> other, IEnumerable<string>? expected = null)
        {
            var expectedSet = (expected ?? standard).ToHashSet();
            var missingOnOther = expectedSet.Except(other).OrderBy(s => s).ToArray();
            var missingOnStandard = other.Except(expectedSet).OrderBy(s => s).ToArray();
            if (missingOnOther.Length > 0 || missingOnStandard.Length > 0)
            {
                failures.Add(
                    $"Missing on {name}:\n  {string.Join("\n  ", missingOnOther)}\n" +
                    $"Missing on QRCodeImageBuilder (present on {name}):\n  {string.Join("\n  ", missingOnStandard)}");
            }
        }
    }

    [Test]
    public async Task EciMode_ExistsOnSymbologiesThatDefineEci()
    {
        await Assert.That(typeof(QRCodeImageBuilder).GetMethods().Any(m => m.Name == "WithEciMode")).IsTrue();
        await Assert.That(typeof(RmQRCodeImageBuilder).GetMethods().Any(m => m.Name == "WithEciMode")).IsTrue();
        await Assert.That(typeof(MicroQRCodeImageBuilder).GetMethods().Any(m => m.Name == "WithEciMode")).IsFalse();
    }

    [Test]
    public async Task StandardOnlyMembers_ExistOnStandard_AndNotOnOthers()
    {
        foreach (var name in standardOnlyMembers)
        {
            await Assert.That(typeof(QRCodeImageBuilder).GetMethods().Any(m => m.Name == name)).IsTrue();
            await Assert.That(typeof(MicroQRCodeImageBuilder).GetMethods().Any(m => m.Name == name)).IsFalse();
            await Assert.That(typeof(RmQRCodeImageBuilder).GetMethods().Any(m => m.Name == name)).IsFalse();
        }
    }

    [Test]
    public async Task MaskPattern_ExistsOnSymbologiesThatSelectAMask()
    {
        await Assert.That(typeof(QRCodeImageBuilder).GetMethods().Any(m => m.Name == "WithMaskPattern")).IsTrue();
        await Assert.That(typeof(MicroQRCodeImageBuilder).GetMethods().Any(m => m.Name == "WithMaskPattern")).IsTrue();
        await Assert.That(typeof(RmQRCodeImageBuilder).GetMethods().Any(m => m.Name == "WithMaskPattern")).IsFalse();
    }

    [Test]
    public async Task VersionRangeOverload_ExistsOnStandardAndMicro_AndNotOnRmqr()
    {
        // The version constraint is one concept, so it is one method name with two
        // overloads rather than two names; rMQR has only the pinned one.
        await Assert.That(HasVersionOverload(typeof(QRCodeImageBuilder), typeof(QRVersionRange))).IsTrue();
        await Assert.That(HasVersionOverload(typeof(MicroQRCodeImageBuilder), typeof(MicroQRVersionRange))).IsTrue();

        await Assert.That(HasVersionOverload(typeof(QRCodeImageBuilder), typeof(int))).IsTrue();
        await Assert.That(HasVersionOverload(typeof(MicroQRCodeImageBuilder), typeof(MicroQRVersion))).IsTrue();
        await Assert.That(HasVersionOverload(typeof(RmQRCodeImageBuilder), typeof(RmQRVersion))).IsTrue();

        var rmqrVersionOverloads = typeof(RmQRCodeImageBuilder).GetMethods().Count(m => m.Name == "WithVersion");
        await Assert.That(rmqrVersionOverloads).IsEqualTo(1);

        static bool HasVersionOverload(Type builder, Type parameterType)
            => builder.GetMethods().Any(m => m.Name == "WithVersion"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == parameterType);
    }

    [Test]
    public async Task Segmentation_ExistsOnEveryBuilder()
    {
        await Assert.That(typeof(QRCodeImageBuilder).GetMethods().Any(m => m.Name == "WithSegmentation")).IsTrue();
        await Assert.That(typeof(MicroQRCodeImageBuilder).GetMethods().Any(m => m.Name == "WithSegmentation")).IsTrue();
        await Assert.That(typeof(RmQRCodeImageBuilder).GetMethods().Any(m => m.Name == "WithSegmentation")).IsTrue();
    }

    [Test]
    public async Task RmqrOnlyMembers_ExistOnRmqr_AndNotOnOthers()
    {
        foreach (var name in rmqrOnlyMembers)
        {
            await Assert.That(typeof(RmQRCodeImageBuilder).GetMethods().Any(m => m.Name == name)).IsTrue();
            await Assert.That(typeof(QRCodeImageBuilder).GetMethods().Any(m => m.Name == name)).IsFalse();
            await Assert.That(typeof(MicroQRCodeImageBuilder).GetMethods().Any(m => m.Name == name)).IsFalse();
        }
    }

    /// <summary>
    /// Public constructors and methods (declared + inherited, static + instance) as
    /// normalized signature strings with symbology-specific types canonicalized.
    /// </summary>
    private static IEnumerable<string> NormalizedSignatures(Type builder)
    {
        foreach (var ctor in builder.GetConstructors())
        {
            yield return $"ctor({ParameterList(ctor.GetParameters(), ctor.Name)})";
        }

        var methods = builder.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(m => m.DeclaringType != typeof(object));
        foreach (var method in methods)
        {
            yield return $"{Normalize(method.ReturnType)} {method.Name}({ParameterList(method.GetParameters(), method.Name)})";
        }
    }

    private static string ParameterList(ParameterInfo[] parameters, string memberName)
        => string.Join(",", parameters.Select(p => NormalizeParameter(p.ParameterType, memberName)));

    /// <summary>
    /// WithVersion legitimately differs in parameter type (int sentinel vs the
    /// symbology version enums); all canonicalize to VERSION for that member only.
    /// </summary>
    private static string NormalizeParameter(Type type, string memberName)
    {
        if (memberName == "WithVersion")
        {
            if (type == typeof(int) || type == typeof(MicroQRVersion) || type == typeof(RmQRVersion))
                return "VERSION";
            if (type == typeof(QRVersionRange) || type == typeof(MicroQRVersionRange))
                return "VERSIONRANGE";
        }
        return Normalize(type);
    }

    private static string Normalize(Type type)
    {
        if (type == typeof(QRCodeData) || type == typeof(MicroQRCodeData) || type == typeof(RmQRCodeData))
            return "SYMBOL_DATA";
        if (type == typeof(QREccLevel) || type == typeof(MicroQREccLevel) || type == typeof(RmQREccLevel))
            return "ECC";
        if (type == typeof(QRSegmentation) || type == typeof(MicroQRSegmentation) || type == typeof(RmQRSegmentation))
            return "SEGMENTATION";
        if (type == typeof(QRCodeImageBuilder) || type == typeof(MicroQRCodeImageBuilder) || type == typeof(RmQRCodeImageBuilder))
            return "SELF";
        return type.FullName ?? type.Name;
    }
}
