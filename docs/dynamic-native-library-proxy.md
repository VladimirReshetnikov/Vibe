# Dynamic Proxy for Unmanaged Libraries

This document proposes an API that exposes functions exported by an unmanaged library through a `dynamic` proxy object. The goal is to offer an experience similar to `TypeExtensions.ToDynamicObject`, which surfaces static members of a managed type, but targeting native libraries loaded at runtime.

## 1. Overview

`TypeExtensions.ToDynamicObject` enables reflection-free access to the static members of a managed `Type`. Many reverse-engineering and automation scenarios also need to call into unmanaged code. Writing P/Invoke declarations for each native function is verbose and inflexible when working with arbitrary libraries. The proposed helper will load a native library by name or full path and return a dynamic proxy where each method maps to an exported function from the library.

```csharp
// Example usage
dynamic kernel32 = NativeLibraryProxy.Load("kernel32.dll");
uint tickCount = kernel32.GetTickCount();
```

The proxy resolves the call to the `GetTickCount` export and returns its result to the caller.  Subsequent calls are cached so that the function pointer and delegate are created only once per unique signature.

## 2. API Surface

```csharp
public static class NativeLibraryProxy
{
    /// <summary>
    /// Loads <paramref name="library"/> and exposes its exports as a dynamic object.
    /// </summary>
    /// <param name="library">File name or absolute path to the native library.</param>
    /// <param name="defaultCallingConvention">Optional calling convention used when none is specified.</param>
    /// <returns>A dynamic object whose members correspond to exported symbols.</returns>
    public static dynamic Load(
        string library,
        CallingConvention defaultCallingConvention = CallingConvention.Winapi);
}
```

- `library` accepts either a bare module name (`"user32"`) or a full path.
- The proxy owns the native handle and implements `IDisposable` so callers can free the library when finished.
- The optional `defaultCallingConvention` controls the `UnmanagedFunctionPointer` attribute applied to generated delegates. Per-call overrides may be added in a future extension.

## 3. Method Resolution and Signatures

### 3.1 Resolution

When a member `proxy.Foo` is invoked:

1. `TryInvokeMember` searches the library exports for a symbol named `Foo` (case sensitive).
2. On the first invocation of a particular `(name, parameter types, return type)` tuple, the proxy obtains the function pointer via `NativeLibrary.GetExport`.
3. A custom delegate type matching the invocation signature is emitted and decorated with `UnmanagedFunctionPointer(defaultCallingConvention, CharSet = CharSet.Unicode)`. `Marshal.GetDelegateForFunctionPointer` creates an instance of that delegate.
4. The delegate is cached and invoked with the provided arguments.

Subsequent calls with the same signature reuse the cached delegate.

### 3.2 Signature Inference

- **Parameter types** are inferred from the runtime types of the arguments supplied in the dynamic call. Only blittable types and `string` are supported initially. `string` values are marshalled as UTF-16 (`LPWStr`) and are intended for the Unicode ("W") variants of Win32 APIs. ANSI or UTF-8 strings are not yet supported but may be in a future revision.
- **Return type** is taken from `InvokeMemberBinder.ReturnType`. The caller **must** specify the expected return type either through an explicit cast or by assigning the result to a typed variable:

```csharp
int result = proxy.Add(1, 2);         // binder.ReturnType == typeof(int)
var ptr = (IntPtr)proxy.GetHandle();  // binder.ReturnType == typeof(IntPtr)
```

  If the binder's return type is `object` (for example `var x = proxy.Add(1,2);`), the proxy assumes the native function returns `IntPtr` and boxes the result. Calling without specifying the return type can therefore lead to stack imbalance or crashes.
- `ref`/`out` parameters, structs with non-blittable fields, and custom marshaling attributes are not supported in the initial version.
- The calling convention used to emit the delegate defaults to the value supplied to `Load`. Future revisions may allow per-call overrides via an attribute object or naming convention such as `proxy.GetTickCount_Cdecl()`.

### 3.3 Error Handling

- If the library cannot be loaded, `DllNotFoundException` is thrown.
- If the requested export is missing, `MissingMethodException` is thrown.
- If parameter or return types are not blittable, `NotSupportedException` is raised.
- Invocation errors from the native code propagate as `SEHException` or other platform-specific failures. Mismatched signatures or calling conventions may corrupt the stack and terminate the process.

## 4. Implementation Plan

1. **Library Loading** – Use `NativeLibrary.Load` and store the handle. Implement `IDisposable` to call `NativeLibrary.Free`.
2. **Dynamic Object** – Create an internal `DynamicLibraryProxy : DynamicObject` similar to `StaticTypeProxy` used by `TypeExtensions`.
3. **Invoke Member** – Override `TryInvokeMember` to perform the resolution steps above and invoke the cached delegate.
4. **Delegate Generation** – Emit delegate types via `TypeBuilder` so they can be decorated with `UnmanagedFunctionPointer` reflecting the selected calling convention and `CharSet.Unicode` for string marshalling. `Marshal.GetDelegateForFunctionPointer` binds the native function to the emitted type.
5. **Caching** – Maintain a dictionary keyed by `(method name, parameter types, return type)` to store generated delegates.
6. **String Marshalling** – For `string` parameters, either rely on automatic marshalling by setting `CharSet = CharSet.Unicode` in the `UnmanagedFunctionPointer` attribute, or manually allocate unmanaged memory with `Marshal.StringToHGlobalUni`, pass the pointer, then free it in a `finally` block after the call. Where possible, a `fixed` statement with `Span<char>` may reduce allocations. Future improvements may allow custom encodings.
7. **Thread Safety** – Guard the delegate cache with `ConcurrentDictionary` to allow concurrent invocations.
8. **Disposal** – Free all allocated delegates and release the library handle when the proxy is disposed. Delegates are managed objects; unloading the library while delegates remain in use is undefined and documented as caller responsibility.

## 5. Usage Examples

```csharp
// Load by module name
using dynamic user32 = NativeLibraryProxy.Load("user32");
int answer = user32.MessageBoxW(IntPtr.Zero, "Text", "Title", 0);

// Load by full path with a different calling convention
using dynamic msvcrt = NativeLibraryProxy.Load(@"C:\\Windows\\System32\\msvcrt.dll", CallingConvention.Cdecl);
double cos = msvcrt.cos(0.0); // 'cos' expects a double and uses cdecl
```

## 6. Limitations and Future Work

- Only supports functions with simple, blittable signatures.
- Requires callers to know and specify the correct return type.
- Does not handle `ref`/`out` parameters, structures, or callbacks.
- No symbol enumeration; functions must be called by exact export name.
- Potential future improvements include:
  - Allow specifying custom marshalling rules per call.
  - Support for `ref`/`out` parameters and struct arguments.
  - Enumerating exports to provide `TryGetMember` proxy access.
  - Lazy unloading of the library once all delegates are collected.

## 7. Testing Strategy

- Unit tests using a small C library compiled for the test run that exports simple functions (addition, returning constants, string length).
- Tests verify:
  - Successful invocation of functions with various primitive signatures.
  - Delegates are cached and reused across calls.
  - Exceptions are thrown for missing exports or unsupported types.
  - Proper disposal frees the library handle.
  - Functions with different calling conventions can be invoked successfully.
  - String parameters round-trip correctly and do not leak memory.
  - Invalid signatures or encodings surface appropriate errors.

## 8. Summary

The `NativeLibraryProxy.Load` helper brings ergonomic dynamic invocation to unmanaged libraries.  It provides a lightweight alternative to manual P/Invoke declarations when working with arbitrary native code, making it particularly useful for reverse-engineering or prototyping scenarios where the set of required functions is not known at compile time.

