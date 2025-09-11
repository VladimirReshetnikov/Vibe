using System;
using System.IO;
using Vibe.Utils;
using Xunit;

namespace Vibe.Utils.Tests;

public class InvocationTests
{
    [Fact]
    public void InvokesVoidMethod()
    {
        dynamic proxy = typeof(Console).ToDynamicObject();
        using var writer = new StringWriter();
        Console.SetOut(writer);
        proxy.WriteLine("Hello");
        Assert.Equal("Hello" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void InvokesNonVoidOverload()
    {
        dynamic proxy = typeof(OverloadedMethods).ToDynamicObject();
        var result = proxy.Foo("bar");
        Assert.Equal("bar", result);
    }
}

public static class OverloadedMethods
{
    public static void Foo(int _) { }
    public static string Foo(string value) => value;
}
