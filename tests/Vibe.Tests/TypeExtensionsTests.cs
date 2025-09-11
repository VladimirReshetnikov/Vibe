using System;
using Vibe.Utils;
using Xunit;

namespace Vibe.Tests;

/// <summary>
/// Tests the <see cref="TypeExtensions.ToDynamicObject"/> method for exposing static members dynamically.
/// </summary>
public static class TypeExtensionsTests
{
    private static dynamic CreateProxy() => typeof(SampleStaticClass).ToDynamicObject();

    /// <summary>
    /// Reads and writes a static field through the dynamic proxy.
    /// </summary>
    [Fact]
    public static void DynamicFieldAccess()
    {
        /* THIS TEST FAILS:
         Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
'Vibe.Tests.SampleStaticClass' does not contain a definition for 'Field'
   at CallSite.Target(Closure, CallSite, Object, Object)
   at System.Dynamic.UpdateDelegates.UpdateAndExecute2[T0,T1,TRet](CallSite site, T0 arg0, T1 arg1)
   at lambda_method50(Closure)
   at Vibe.Utils.TypeExtensions.StaticTypeProxy.TrySetMember(SetMemberBinder binder, Object value) in C:\Users\vresh\source\repos\Vibe\Vibe.Utils\TypeExtensions.cs:line 94
   at CallSite.Target(Closure, CallSite, Object, Int32)
   at System.Dynamic.UpdateDelegates.UpdateAndExecute2[T0,T1,TRet](CallSite site, T0 arg0, T1 arg1)
   at Vibe.Tests.TypeExtensionsTests.DynamicFieldAccess() in C:\Users\vresh\source\repos\Vibe\tests\Vibe.Tests\TypeExtensionsTests.cs:line 21
   at System.RuntimeMethodHandle.InvokeMethod(Object target, Void** arguments, Signature sig, Boolean isConstructor)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)

   TODO: Fix it. Implement missing parts in `ToDynamicObject`
   */
        dynamic proxy = CreateProxy();
        proxy.Field = 42;

        Assert.Equal(42, proxy.Field);
        Assert.Equal(42, SampleStaticClass.Field);
    }

    /// <summary>
    /// Reads and writes a static property through the dynamic proxy.
    /// </summary>
    [Fact]
    public static void DynamicPropertyAccess()
    {
        /* THIS TEST FAILS:
         Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
'Vibe.Tests.SampleStaticClass' does not contain a definition for 'Property'
   at CallSite.Target(Closure, CallSite, Object, Object)
   at System.Dynamic.UpdateDelegates.UpdateAndExecute2[T0,T1,TRet](CallSite site, T0 arg0, T1 arg1)
   at lambda_method53(Closure)
   at Vibe.Utils.TypeExtensions.StaticTypeProxy.TrySetMember(SetMemberBinder binder, Object value) in C:\Users\vresh\source\repos\Vibe\Vibe.Utils\TypeExtensions.cs:line 94
   at CallSite.Target(Closure, CallSite, Object, String)
   at System.Dynamic.UpdateDelegates.UpdateAndExecute2[T0,T1,TRet](CallSite site, T0 arg0, T1 arg1)
   at Vibe.Tests.TypeExtensionsTests.DynamicPropertyAccess() in C:\Users\vresh\source\repos\Vibe\tests\Vibe.Tests\TypeExtensionsTests.cs:line 49
   at System.RuntimeMethodHandle.InvokeMethod(Object target, Void** arguments, Signature sig, Boolean isConstructor)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)

    TODO: Fix it. Implement missing parts in `ToDynamicObject`
*/
        dynamic proxy = CreateProxy();
        proxy.Property = "changed";

        Assert.Equal("changed", proxy.Property);
        Assert.Equal("changed", SampleStaticClass.Property);
    }

    /// <summary>
    /// Invokes normal static methods.
    /// </summary>
    [Fact]
    public static void DynamicMethodInvocation()
    {
        dynamic proxy = CreateProxy();

        Assert.Equal("hello", proxy.Echo("hello"));
    }

    /// <summary>
    /// Invokes generic methods with explicit type arguments.
    /// </summary>
    [Fact]
    public static void DynamicGenericMethodExplicitType()
    {
        dynamic proxy = CreateProxy();

        int result = proxy.GenericEcho<int>(5);

        Assert.Equal(5, result);
    }

    /// <summary>
    /// Invokes generic methods using type inference.
    /// </summary>
    [Fact]
    public static void DynamicGenericMethodTypeInference()
    {
        dynamic proxy = CreateProxy();

        string result = proxy.GenericEcho("text");

        Assert.Equal("text", result);
    }

    [Fact]
    public static void ProxyProvidesNestedStaticType()
    {
        dynamic proxy = typeof(Outer).ToDynamicObject();
        int result = proxy.Inner.AddOne(1);
        Assert.Equal(2, result);
    }

    /// <summary>
    /// Ensures that providing a null type throws an <see cref="ArgumentNullException"/>.
    /// </summary>
    [Fact]
    public static void NullTypeThrowsArgumentNullException()
    {
        Type? type = null;
        Assert.Throws<ArgumentNullException>(() => type!.ToDynamicObject());
    }

    /// <summary>
    /// Invokes overloaded static methods through the dynamic proxy.
    /// </summary>
    [Fact]
    public static void DynamicMethodOverloads()
    {
        dynamic proxy = typeof(OverloadedSample).ToDynamicObject();
        Assert.Equal("int:5", proxy.DoWork(5));
        Assert.Equal("string:test", proxy.DoWork("test"));
    }
}

public static class SampleStaticClass
{
    public static int Field;
    public static string? Property { get; set; }
    public static string Echo(string input) => input;
    public static T GenericEcho<T>(T value) => value;
}

public static class Outer
{
    public static class Inner
    {
        public static int AddOne(int value) => value + 1;
    }
}

public static class OverloadedSample
{
    public static string DoWork(int value) => $"int:{value}";
    public static string DoWork(string value) => $"string:{value}";
}
