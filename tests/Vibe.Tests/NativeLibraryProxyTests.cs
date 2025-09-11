using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Vibe.Utils;
using Xunit;

namespace Vibe.Tests;

public sealed class NativeLibraryProxyTests
{
    private static dynamic LoadLibrary()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null!;
        return NativeLibraryProxy.Load("kernel32.dll");
    }

    [Fact]
    public void InvokesSimpleFunction()
    {
        dynamic lib = LoadLibrary();
        if (lib is null)
            return;
        try
        {
            int result = (int)lib.MulDiv(5, 4, 2);
            Assert.Equal(10, result);
        }
        finally
        {
            ((IDisposable)lib).Dispose();
        }
    }

    [Fact]
    public void InvokesFunctionWithStringParameter()
    {
        dynamic lib = LoadLibrary();
        if (lib is null)
            return;
        try
        {
            int len = (int)lib.lstrlenW("hello");
            Assert.Equal(5, len);
        }
        finally
        {
            ((IDisposable)lib).Dispose();
        }
    }

    [Fact]
    public void ThrowsForMissingExport()
    {
        dynamic lib = LoadLibrary();
        if (lib is null)
            return;
        try
        {
            Assert.Throws<MissingMethodException>(() => lib.does_not_exist());
        }
        finally
        {
            ((IDisposable)lib).Dispose();
        }
    }

    private struct NonBlittable
    {
        public string Text;
    }

    [Fact]
    public void ThrowsForUnsupportedParameterType()
    {
        dynamic lib = LoadLibrary();
        if (lib is null)
            return;
        try
        {
            Assert.Throws<NotSupportedException>(() => lib.MulDiv(new NonBlittable { Text = "x" }, 1, 2));
        }
        finally
        {
            ((IDisposable)lib).Dispose();
        }
    }

    [Fact]
    public void CachesDelegates()
    {
        dynamic lib = LoadLibrary();
        if (lib is null)
            return;
        try
        {
            int a = (int)lib.MulDiv(1, 2, 1);
            int b = (int)lib.MulDiv(3, 4, 3);
            object obj = lib;
            var field = obj.GetType().GetField("delegateCache", BindingFlags.NonPublic | BindingFlags.Instance);
            var dict = (IDictionary)field!.GetValue(obj)!;
            Assert.Single(dict);
            Assert.Equal(2, a);
            Assert.Equal(4, b);
        }
        finally
        {
            ((IDisposable)lib).Dispose();
        }
    }
}

