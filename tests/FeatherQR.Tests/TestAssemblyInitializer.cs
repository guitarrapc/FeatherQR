using System.Runtime.CompilerServices;
using System.Text;

namespace FeatherQR.Tests;

internal static class TestAssemblyInitializer
{
    /// <summary>
    /// ZXing.Net reads a Kanji segment through the Shift_JIS encoding, which .NET offers only once the code-pages provider is registered.
    /// The generators write Kanji now, so every ZXing.Net test in this process runs with it, registered here before any test, rather than depending on which test happened to run first.
    /// </summary>
    [ModuleInitializer]
    internal static void RegisterCodePages() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
