using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR.Tests;

/// <summary>
/// Two pages of native memory with the second made inaccessible, so a span that ends at the page boundary ends where readable memory
/// ends: a kernel that reads past its input touches the protected page and takes the process down, where over managed memory the read
/// would land on the next object unseen. For loads wider than what they use (an 8-byte load for three digits, sixteen characters read for
/// twelve written), whose guard is the only thing keeping them inside the input.
/// </summary>
/// <remarks>Windows (VirtualAlloc) and Linux and macOS (mmap); <see cref="IsSupported"/> is false elsewhere.</remarks>
internal sealed class PageEndMemory : IDisposable
{
    private readonly nint _address;
    private readonly int _pageSize = Environment.SystemPageSize;

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public PageEndMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            _address = VirtualAlloc(0, (nuint)(2 * _pageSize), MemCommit | MemReserve, PageReadWrite);
            if (_address == 0)
                throw new InvalidOperationException($"VirtualAlloc failed: {Marshal.GetLastPInvokeError()}");
            if (!VirtualProtect(_address + _pageSize, (nuint)_pageSize, PageNoAccess, out _))
            {
                var error = Marshal.GetLastPInvokeError();
                VirtualFree(_address, 0, MemRelease);
                throw new InvalidOperationException($"VirtualProtect failed: {error}");
            }
        }
        else
        {
            var anonymous = OperatingSystem.IsMacOS() ? 0x1000 : 0x20;
            _address = mmap(0, (nuint)(2 * _pageSize), ProtRead | ProtWrite, MapPrivate | anonymous, -1, 0);
            if (_address == -1)
                throw new InvalidOperationException($"mmap failed: {Marshal.GetLastPInvokeError()}");
            if (mprotect(_address + _pageSize, (nuint)_pageSize, ProtNone) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                munmap(_address, (nuint)(2 * _pageSize));
                throw new InvalidOperationException($"mprotect failed: {error}");
            }
        }
    }

    /// <summary>A span of <paramref name="text"/>'s characters whose last character is the last readable one.</summary>
    public ReadOnlySpan<char> AtPageEnd(ReadOnlySpan<char> text)
    {
        if (text.Length * sizeof(char) > _pageSize)
            throw new ArgumentOutOfRangeException(nameof(text), "longer than a page");
        var start = _address + _pageSize - text.Length * sizeof(char);
        var span = MemoryMarshal.CreateSpan(ref Unsafe.AddByteOffset(ref Unsafe.NullRef<char>(), start), text.Length);
        text.CopyTo(span);
        return span;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            VirtualFree(_address, 0, MemRelease);
        else
            munmap(_address, (nuint)(2 * _pageSize));
    }

    private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemRelease = 0x8000, PageReadWrite = 0x04, PageNoAccess = 0x01;
    private const int ProtNone = 0, ProtRead = 1, ProtWrite = 2, MapPrivate = 2;

    [DllImport("kernel32", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);

    [DllImport("libc", SetLastError = true)]
    private static extern nint mmap(nint address, nuint length, int protection, int flags, int fd, nint offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(nint address, nuint length, int protection);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(nint address, nuint length);
}
