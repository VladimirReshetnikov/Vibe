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
}
