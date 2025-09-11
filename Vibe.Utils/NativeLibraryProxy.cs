// SPDX-License-Identifier: MIT-0

using System.Collections.Concurrent;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vibe.Utils;

/// <summary>
/// Provides helpers for dynamically invoking exports from unmanaged libraries.
/// </summary>
public static class NativeLibraryProxy
{
    /// <summary>
    /// Loads <paramref name="library"/> and exposes its exports as a dynamic object.
    /// </summary>
    /// <param name="library">File name or absolute path to the native library.</param>
    /// <param name="defaultCallingConvention">Optional calling convention used when none is specified.</param>
    /// <returns>A dynamic object whose members correspond to exported symbols.</returns>
    public static dynamic Load(string library, CallingConvention defaultCallingConvention = CallingConvention.Winapi)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(library);
        return new DynamicLibraryProxy(library, defaultCallingConvention);
    }

    private sealed class DynamicLibraryProxy : DynamicObject, IDisposable
    {
        private IntPtr handle;
        private readonly CallingConvention callingConvention;
        private readonly ModuleBuilder module;
        private readonly ConcurrentDictionary<Signature, Delegate> delegateCache = new();

        public DynamicLibraryProxy(string library, CallingConvention callingConvention)
        {
            handle = NativeLibrary.Load(library);
            this.callingConvention = callingConvention;
            var asmName = new AssemblyName("NativeLibraryProxy.DynamicDelegates." + Guid.NewGuid().ToString("N"));
            var asm = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
            module = asm.DefineDynamicModule(asmName.Name!);
        }

        public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
        {
            args ??= Array.Empty<object>();
            var parameterTypes = args.Select(a => a?.GetType() ?? typeof(object)).ToArray();
            var returnType = binder.ReturnType == typeof(object) ? typeof(IntPtr) : binder.ReturnType;

            ValidateTypes(parameterTypes, returnType);

            var signature = new Signature(binder.Name, parameterTypes, returnType);

            var del = delegateCache.GetOrAdd(signature, sig => CreateDelegate(sig));

            result = del.DynamicInvoke(args);
            return true;
        }

        private Delegate CreateDelegate(Signature sig)
        {
            IntPtr proc;
            try
            {
                proc = NativeLibrary.GetExport(handle, sig.Name);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new MissingMethodException($"Export '{sig.Name}' was not found.", ex);
            }

            var delegateType = EmitDelegateType(sig.ParameterTypes, sig.ReturnType);
            return Marshal.GetDelegateForFunctionPointer(proc, delegateType);
        }

        private Type EmitDelegateType(Type[] parameterTypes, Type returnType)
        {
            string name = "NativeDelegate_" + Guid.NewGuid().ToString("N");
            var tb = module.DefineType(name, TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Public, typeof(MulticastDelegate));

            var ctor = tb.DefineConstructor(MethodAttributes.RTSpecialName | MethodAttributes.Public | MethodAttributes.HideBySig,
                CallingConventions.Standard, new[] { typeof(object), typeof(IntPtr) });
            ctor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

            var mb = tb.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
                returnType, parameterTypes);
            mb.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

            var attrCtor = typeof(UnmanagedFunctionPointerAttribute).GetConstructor(new[] { typeof(CallingConvention) })!;
            var charSetField = typeof(UnmanagedFunctionPointerAttribute).GetField(nameof(UnmanagedFunctionPointerAttribute.CharSet))!;
            var cab = new CustomAttributeBuilder(attrCtor, new object[] { callingConvention }, Array.Empty<PropertyInfo>(), Array.Empty<object>(), new[] { charSetField }, new object[] { CharSet.Unicode });
            tb.SetCustomAttribute(cab);

            return tb.CreateType();
        }

        private static void ValidateTypes(Type[] parameters, Type returnType)
        {
            foreach (var t in parameters)
            {
                if (!IsSupportedType(t))
                    throw new NotSupportedException($"Parameter type {t} is not supported.");
            }

            if (!IsSupportedType(returnType))
                throw new NotSupportedException($"Return type {returnType} is not supported.");
        }

        private static bool IsSupportedType(Type type, bool allowString = true)
        {
            if (allowString && type == typeof(string))
                return true;
            if (type.IsPointer)
                return true;
            if (type.IsPrimitive || type.IsEnum)
                return true;
            if (type.IsValueType)
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!IsSupportedType(field.FieldType, allowString: false))
                        return false;
                }
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref handle, IntPtr.Zero);
            if (h != IntPtr.Zero)
            {
                NativeLibrary.Free(h);
            }
        }

        private readonly record struct Signature(string Name, Type[] ParameterTypes, Type ReturnType)
        {
            public bool Equals(Signature other) =>
                Name == other.Name && ReturnType == other.ReturnType && ParameterTypes.SequenceEqual(other.ParameterTypes);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(Name);
                hash.Add(ReturnType);
                foreach (var t in ParameterTypes)
                    hash.Add(t);
                return hash.ToHashCode();
            }
        }
    }
}

