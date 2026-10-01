namespace FeatherQR.Tests.DependencyProbes.Targets;

// What the probes in FeatherQR.Tests.DependencyProbes name (SymbologyDependencyProbes.cs), each target by one probe only

public class BaseTarget;

public interface IInterfaceTarget;

public struct FieldTarget;

public class ParameterTarget;

public class ReturnTarget;

public class ArrayTarget;

public class MultiDimensionalArrayTarget;

public struct ByReferenceTarget;

public struct ModifiedTarget;

public struct PointerTarget;

public struct FunctionPointerTarget;

public struct FunctionPointerReturnTarget;

public class LocalTarget;

public class CatchTarget : Exception;

public class TypeTokenTarget;

public class CastTarget;

public class LambdaTarget;

public class NestedTypeTarget;

public static class FieldTokenTarget
{
    public static int Value;
}

public class BridgedMethodTarget;

public class BridgedFieldTarget;

public static class MethodTokenTarget
{
    public static void Run()
    {
    }
}

public class GenericTypeTarget<T>;

public class GenericArgumentTarget;

public class MethodArgumentTarget;

public static class GenericMethodTarget
{
    public static T? Make<T>() => default;
}

public class MemberOwnerTarget<T>
{
    public void Take(MemberSignatureTarget? target)
    {
    }
}

public class MemberSignatureTarget;

public class FieldHolderTarget<T>
{
    public FieldSignatureTarget? Field;
}

public class FieldSignatureTarget;

public class TypeAttributeTarget : Attribute;

public class MethodAttributeTarget : Attribute;

public class FieldAttributeTarget : Attribute;

public class ParameterAttributeTarget : Attribute;

public class PropertyAttributeTarget : Attribute;

public class EventAttributeTarget : Attribute;

public class GenericParameterAttributeTarget : Attribute;

public class AttributeArgumentTarget;

public class AttributeGenericArgumentTarget;

public struct AttributePointerTarget;

public class AttributeFirstArgumentTarget;

public class AttributeSecondArgumentTarget;

public class AttributeGenericOwnerTarget<T>;

public class AttributeOwnArgumentTarget;

public class AttributeLongNameTargetWhoseFullNameRunsPastOneHundredAndTwentySevenBytesSoItsLengthTakesTwoBytes;

public class AttributeNestedOwnerTarget
{
    public class Nested;
}

public interface ITypeConstraintTarget;

public interface IMethodConstraintTarget;
