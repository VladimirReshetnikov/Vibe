using System;
using Vibe.Utils;
using Xunit;

namespace Vibe.Utils.Tests;

/// <summary>
/// Tests the <see cref="TypeExtensions.ToDynamicObject"/> method for exposing static members dynamically.
/// </summary>
public static class TypeExtensionsTests
{
    private static dynamic CreateProxy() => typeof(SampleStaticClass).ToDynamicObject();

    /// <summary>
    /// Attempts to read a static field. This should work but is currently not supported.
    /// </summary>
    [Fact(Skip = "Static field access not yet supported")]
    public static void DynamicFieldAccess()
    {
        SampleStaticClass.Field = 42;
        dynamic proxy = CreateProxy();

        Assert.Equal(42, proxy.Field);
    }

    /// <summary>
    /// Attempts to read a static property. This should work but is currently not supported.
    /// </summary>
    [Fact(Skip = "Static property access not yet supported")]
    public static void DynamicPropertyAccess()
    {
        SampleStaticClass.Property = "changed";
        dynamic proxy = CreateProxy();

        Assert.Equal("changed", proxy.Property);
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
