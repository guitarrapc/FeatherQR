using System.Diagnostics;
using FeatherQR.Tests.DependencyBridge;
using FeatherQR.Tests.DependencyProbes.Targets;

namespace FeatherQR.Tests.DependencyProbes;

// One kind of reference each, to a target of its own, for SymbologyDependencyTest's reader: a kind it stops reading fails that
// probe's case. Each names its target in one place of the metadata. A pinned local and a call through a function pointer have no
// probe: only unsafe code makes them, and this assembly is compiled without it.

#pragma warning disable CS0824 // an extern constructor has no body, so no call to the base's constructor names the base type too
public class BaseProbe : BaseTarget
{
    public extern BaseProbe();
}
#pragma warning restore CS0824

public class InterfaceProbe : IInterfaceTarget;

public class FieldProbe
{
    public FieldTarget Field;
}

public static class ParameterProbe
{
    public static void Take(ParameterTarget? target)
    {
    }
}

public static class ReturnProbe
{
    public static ReturnTarget? Give() => null;
}

public static class ArrayProbe
{
    public static void Take(ArrayTarget[]? targets)
    {
    }
}

public static class MultiDimensionalArrayProbe
{
    public static void Take(MultiDimensionalArrayTarget[,]? targets)
    {
    }
}

public static class ByReferenceProbe
{
    public static void Take(ref ByReferenceTarget target)
    {
    }
}

/// <summary>An <c>in</c> parameter of an interface method carries a required modifier.</summary>
public interface IModifiedProbe
{
    void Take(in ModifiedTarget target);
}

public static class PointerProbe
{
    public static Type Name() => typeof(PointerTarget*);
}

public static class FunctionPointerProbe
{
    public static Type Name() => typeof(delegate*<FunctionPointerTarget, FunctionPointerReturnTarget>);
}

public static class LocalProbe
{
    public static bool Hold(int count)
    {
        LocalTarget? held = null;
        for (var i = 0; i < count; i++)
            held = null;
        return held is null;
    }
}

public static class CatchProbe
{
    public static void Guard()
    {
        try
        {
            Console.Out.Flush();
        }
        catch (CatchTarget)
        {
        }
    }
}

public static class TypeTokenProbe
{
    public static Type Name() => typeof(TypeTokenTarget);
}

/// <summary>A lambda's body is compiled into a type nested in this one.</summary>
public static class LambdaProbe
{
    public static Func<Type> Make() => () => typeof(LambdaTarget);
}

public static class NestedTypeProbe
{
    public static class Inner
    {
        public static Type Name() => typeof(NestedTypeTarget);
    }
}

public static class CastProbe
{
    public static bool Check(object value) => value is CastTarget;
}

public static class FieldTokenProbe
{
    public static int Read() => FieldTokenTarget.Value;
}

public static class MethodTokenProbe
{
    public static void Call() => MethodTokenTarget.Run();
}

public static class BridgedMethodProbe
{
    public static object? Call() => MemberBridge.Get();
}

public static class BridgedFieldProbe
{
    public static object? Read() => MemberBridge.Field;
}

public static class GenericTypeProbe
{
    public static object Make() => new GenericTypeTarget<int>();
}

public static class GenericArgumentProbe
{
    public static int Count() => new List<GenericArgumentTarget>().Count;
}

public static class MethodArgumentProbe
{
    public static int Count() => Array.Empty<MethodArgumentTarget>().Length;
}

public static class GenericMethodProbe
{
    public static int Call() => GenericMethodTarget.Make<int>();
}

public static class MemberSignatureProbe
{
    public static void Call(MemberOwnerTarget<int> owner) => owner.Take(null);
}

public static class MemberFieldSignatureProbe
{
    public static object? Read(FieldHolderTarget<int> holder) => holder.Field;
}

[TypeAttributeTarget]
public class TypeAttributeProbe;

public static class MethodAttributeProbe
{
    [MethodAttributeTarget]
    public static void Marked()
    {
    }
}

public class FieldAttributeProbe
{
    [FieldAttributeTarget]
    public int Field;
}

public static class ParameterAttributeProbe
{
    public static void Take([ParameterAttributeTarget] int value)
    {
    }
}

public class PropertyAttributeProbe
{
    [PropertyAttributeTarget]
    public int Value => 0;
}

public class EventAttributeProbe
{
    [EventAttributeTarget]
    public event Action Raised
    {
        add { }
        remove { }
    }
}

public class GenericParameterAttributeProbe<[GenericParameterAttributeTarget] T>;

/// <summary>A <see cref="Type"/> argument is stored as the type's name.</summary>
[DebuggerTypeProxy(typeof(AttributeArgumentTarget))]
public class AttributeArgumentProbe;

/// <summary>A generic type's name carries its arguments' names.</summary>
[DebuggerTypeProxy(typeof(List<AttributeGenericArgumentTarget>))]
public class AttributeGenericArgumentProbe;

/// <summary>A nested type's name follows its declaring type's.</summary>
[DebuggerTypeProxy(typeof(AttributeNestedOwnerTarget.Nested))]
public class AttributeNestedProbe;

/// <summary>A pointer type's name is its element's and a star.</summary>
[DebuggerTypeProxy(typeof(AttributePointerTarget*[]))]
public class AttributePointerProbe;

/// <summary>Several generic arguments are separated by commas.</summary>
[DebuggerTypeProxy(typeof(Dictionary<AttributeFirstArgumentTarget, AttributeSecondArgumentTarget>))]
public class AttributeGenericArgumentsProbe;

/// <summary>A generic type of this assembly is named without its assembly, its argument in brackets.</summary>
[DebuggerTypeProxy(typeof(AttributeGenericOwnerTarget<AttributeOwnArgumentTarget>))]
public class AttributeOwnGenericProbe;

/// <summary>A name of 128 bytes or more takes two bytes to give its length.</summary>
[DebuggerTypeProxy(typeof(AttributeLongNameTargetWhoseFullNameRunsPastOneHundredAndTwentySevenBytesSoItsLengthTakesTwoBytes))]
public class AttributeLongNameProbe;

public class TypeConstraintProbe<T> where T : ITypeConstraintTarget;

public static class MethodConstraintProbe
{
    public static void Take<T>() where T : IMethodConstraintTarget
    {
    }
}
