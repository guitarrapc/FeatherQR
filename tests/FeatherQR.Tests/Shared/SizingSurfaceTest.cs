using System.Reflection;

namespace FeatherQR.Tests;

/// <summary>
/// The shape of the sizing surface, as decided in specs/qrcode-symbologies.md: asking
/// "does this fit, and how big is it" is a <c>Try</c> operation, because "it does not
/// fit" is an ordinary data-dependent answer rather than a defect.
/// </summary>
/// <remarks>
/// This is a shape test, not a behaviour test. The throwing overloads that 1.1.1 released
/// were removed in 2.0.0 after a deprecation cycle, and a throwing convenience is exactly
/// the kind of thing someone re-adds in good faith, so the absence is asserted rather than
/// assumed.
/// </remarks>
public class SizingSurfaceTest
{
    private static MethodInfo[] PublicStatic(Type generator, string name)
        => [.. generator.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == name)];

    /// <summary>
    /// rMQR never shipped a throwing sizing method; Standard QR and Micro QR carried one
    /// through the 1.2.0 deprecation cycle and lost it in 2.0.0. None of the three has one.
    /// </summary>
    [Test]
    [Arguments(typeof(QRCodeGenerator))]
    [Arguments(typeof(MicroQRCodeGenerator))]
    [Arguments(typeof(RmQRCodeGenerator))]
    public async Task NoGenerator_HasAThrowingSizingMethod(Type generator)
    {
        await Assert.That(PublicStatic(generator, "GetRequiredBufferSize").Length).IsEqualTo(0);
    }

    /// <summary>
    /// Exactly one non-throwing sizing overload per symbology, and it takes the options
    /// struct. The parameter list <c>Try</c> overloads added in 1.2.0 were deleted before
    /// release rather than shipped and frozen.
    /// </summary>
    [Test]
    [Arguments(typeof(QRCodeGenerator), typeof(QRCodeGeneratorOptions))]
    [Arguments(typeof(MicroQRCodeGenerator), typeof(MicroQRCodeGeneratorOptions))]
    [Arguments(typeof(RmQRCodeGenerator), typeof(RmQRCodeGeneratorOptions))]
    public async Task NonThrowingSizing_IsExactlyOneOverload_TakingOptions(Type generator, Type optionsType)
    {
        var tries = PublicStatic(generator, "TryGetRequiredBufferSize");
        await Assert.That(tries.Length).IsEqualTo(1);

        var last = tries[0].GetParameters()[^1];
        await Assert.That(last.ParameterType).IsEqualTo(optionsType.MakeByRefType());

        // Sizing is the one place the options argument is optional on every symbology, so
        // TryGetRequiredBufferSize(text, ecc, out size) is the shortest correct call.
        await Assert.That(last.IsOptional).IsTrue();

        // The replacement must not itself be deprecated.
        await Assert.That(tries[0].GetCustomAttribute<ObsoleteAttribute>()).IsNull();
    }

    /// <summary>
    /// Every <c>Create</c> overload takes the options struct. The 1.1.1 parameter lists
    /// (<c>utf8BOM</c>, <c>eciMode</c>, <c>requestedVersion</c>, <c>quietZoneSize</c>) were
    /// frozen through 1.2.0 and removed in 2.0.0; rMQR never had them, and the three
    /// generators now have the same shape.
    /// </summary>
    [Test]
    [Arguments(typeof(QRCodeGenerator), typeof(QRCodeGeneratorOptions))]
    [Arguments(typeof(MicroQRCodeGenerator), typeof(MicroQRCodeGeneratorOptions))]
    [Arguments(typeof(RmQRCodeGenerator), typeof(RmQRCodeGeneratorOptions))]
    public async Task EveryCreateOverload_EndsWithOptionalOptions(Type generator, Type optionsType)
    {
        var creates = generator.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Create", StringComparison.Ordinal))
            .ToArray();

        await Assert.That(creates.Length).IsGreaterThan(0);

        foreach (var create in creates)
        {
            var last = create.GetParameters()[^1];
            await Assert.That(last.ParameterType).IsEqualTo(optionsType.MakeByRefType())
                .Because($"{generator.Name}.{create.Name} must take its configuration as {optionsType.Name}");
            await Assert.That(last.IsOptional).IsTrue()
                .Because($"{generator.Name}.{create.Name} must be callable without options");
        }
    }
}
