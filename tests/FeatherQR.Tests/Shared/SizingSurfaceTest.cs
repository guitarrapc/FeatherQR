using System.Reflection;

namespace FeatherQR.Tests;

/// <summary>
/// The shape of the sizing surface, as decided in specs/qrcode-symbologies.md: asking
/// "does this fit, and how big is it" is a <c>Try</c> operation, because "it does not
/// fit" is an ordinary data-dependent answer rather than a defect.
/// </summary>
/// <remarks>
/// This is a shape test, not a behaviour test. A throwing convenience is exactly the kind
/// of thing someone adds in good faith, so the absence is asserted rather than assumed.
/// </remarks>
public class SizingSurfaceTest
{
    private static MethodInfo[] PublicStatic(Type generator, string name)
        => [.. generator.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == name)];

    /// <summary>None of the three generators has a throwing sizing method.</summary>
    [Test]
    [Arguments(typeof(QRCodeGenerator))]
    [Arguments(typeof(MicroQRCodeGenerator))]
    [Arguments(typeof(RmQRCodeGenerator))]
    public async Task NoGenerator_HasAThrowingSizingMethod(Type generator)
    {
        await Assert.That(PublicStatic(generator, "GetRequiredBufferSize").Length).IsEqualTo(0);
    }

    /// <summary>
    /// One non-throwing sizing overload per kind of content, text and bytes, and it takes
    /// the options struct, not a parameter list.
    /// </summary>
    [Test]
    [Arguments(typeof(QRCodeGenerator), typeof(QRCodeGeneratorOptions))]
    [Arguments(typeof(MicroQRCodeGenerator), typeof(MicroQRCodeGeneratorOptions))]
    [Arguments(typeof(RmQRCodeGenerator), typeof(RmQRCodeGeneratorOptions))]
    public async Task NonThrowingSizing_IsOneOverloadPerContent_TakingOptions(Type generator, Type optionsType)
    {
        var tries = PublicStatic(generator, "TryGetRequiredBufferSize");
        var contents = tries.Select(m => m.GetParameters()[0].ParameterType).ToArray();
        await Assert.That(contents.Distinct().Count()).IsEqualTo(tries.Length).Because("one overload a kind of content");
        await Assert.That(contents.All(t => t == typeof(ReadOnlySpan<char>) || t == typeof(ReadOnlySpan<byte>))).IsTrue();
        await Assert.That(contents.Contains(typeof(ReadOnlySpan<char>))).IsTrue();

        foreach (var sizing in tries)
        {
            var options = OptionsParameter(sizing);
            await Assert.That(options.ParameterType).IsEqualTo(optionsType.MakeByRefType());

            // Sizing is the one place the options argument is optional on every symbology, so
            // TryGetRequiredBufferSize(text, ecc, out size) is the shortest correct call.
            await Assert.That(options.IsOptional).IsTrue();

            // It is the only sizing call for its content, so it must not be deprecated.
            await Assert.That(sizing.GetCustomAttribute<ObsoleteAttribute>()).IsNull();
        }
    }

    /// <summary>
    /// Every <c>Create</c> overload takes the options struct as its last, optional
    /// parameter, so the three generators have the same shape and none takes its
    /// configuration as a parameter list. A bytes overload under ECI follows it with the
    /// optional assignment, which describes the bytes rather than how to encode them.
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
            var options = OptionsParameter(create);
            await Assert.That(options.ParameterType).IsEqualTo(optionsType.MakeByRefType())
                .Because($"{generator.Name}.{create.Name} must take its configuration as {optionsType.Name}");
            await Assert.That(options.IsOptional).IsTrue()
                .Because($"{generator.Name}.{create.Name} must be callable without options");
        }
    }

    /// <summary>The options parameter: the last, or the one before an optional <c>int? eciAssignment</c> after a bytes payload.</summary>
    private static ParameterInfo OptionsParameter(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var last = parameters[^1];
        var isAssignment = last.Name == "eciAssignment" && last.ParameterType == typeof(int?) && last.IsOptional
            && parameters[0].ParameterType == typeof(ReadOnlySpan<byte>);
        return isAssignment ? parameters[^2] : last;
    }
}
