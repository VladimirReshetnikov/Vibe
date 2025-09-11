using Vibe.Utils;
using Xunit;

namespace Vibe.Decompiler.Tests;

public static class TypeExtensionsTests
{
    public static class Outer
    {
        public static class Inner
        {
            public static int AddOne(int value) => value + 1;
        }
    }

    [Fact]
    public static void ProxyProvidesNestedStaticType()
    {
        dynamic proxy = typeof(Outer).ToDynamicObject();
        int result = proxy.Inner.AddOne(1);
        Assert.Equal(2, result);
    }
}
