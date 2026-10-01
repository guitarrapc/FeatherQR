using FeatherQR.Tests.DependencyProbes.Targets;

namespace FeatherQR.Tests.DependencyBridge;

// Members of a type outside the probes' namespaces, as the library's root namespace is outside FeatherQR.Internals: the reader
// does not read this type's own signatures, so a probe that calls or reads one of its members names the member's types through
// the member's signature only (SymbologyDependencyProbes.cs)
public static class MemberBridge
{
    public static BridgedMethodTarget? Get() => null;

    public static BridgedFieldTarget? Field;
}
