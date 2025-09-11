# `__members__` Property for Dynamic Type Proxies

## Overview

`TypeExtensions.ToDynamicObject()` exposes the static members of a `Type` through a `dynamic` proxy.  Today callers must know
the available members ahead of time; there is no built‑in way to discover them without using reflection.  This proposal adds a
synthetic property named `__members__` to the proxy that enumerates all accessible members along with their signatures and return
types.

The property is read‑only and excluded from the enumeration so it does not list itself.

## Goals

- Offer reflection‑free introspection of members exposed through the proxy.
- Provide machine‑readable metadata for IDE completion, documentation generators, or scripting hosts.
- Keep the surface simple enough to consume from `dynamic` code in C# and other .NET languages.

## Returned Format

`__members__` returns an `IReadOnlyList<MemberDescriptor>`.  Each element describes a single member and has the following shape:

```csharp
public sealed record MemberDescriptor(
    string Name,
    MemberKind Kind,
    string Signature,
    Type? ReturnType,
    IReadOnlyList<ParameterDescriptor> Parameters);

public enum MemberKind { Method, Property, Field, Event }

public sealed record ParameterDescriptor(
    string Name,
    Type Type,
    bool IsOptional = false,
    object? DefaultValue = null);
```

- **Name** – the member name as seen by the dynamic caller.
- **Kind** – categorises the member (method, property, field, event).  Additional kinds may be added in the future.
- **Signature** – friendly C#‑style signature, e.g. `"int Add(int a, int b)"`.
- **ReturnType** – the CLR `Type` returned by the member; `null` for `void` methods.
- **Parameters** – sequence describing each parameter; empty for fields and parameterless properties.

Example usage:

```csharp
dynamic console = typeof(Console).ToDynamicObject();
foreach (var m in console.__members__)
    Console.WriteLine($"{m.Kind} {m.Signature}");
```

This prints entries such as `Method void WriteLine(string value)` or `Property TextWriter Out`.

## Alternatives Considered

### 1. Reflection `MemberInfo`
Returning the underlying `MemberInfo` objects directly would provide maximum detail but undermines the goal of a reflection‑free API
and leaks implementation details of the proxy.

### 2. `dynamic` objects or dictionaries
Each descriptor could be an anonymous dynamic object (`new { name, returnType, ... }`) or `Dictionary<string, object?>`.  This is
flexible but lacks discoverability and encourages stringly‑typed access.  Records offer compile‑time checking for consumers who
cast results to concrete types.

### 3. String‑only representation
A minimal option is to return a list of signature strings.  While compact, it makes programmatic analysis difficult and requires
callers to parse strings to extract types and parameters.

## Implementation Notes

- The proxy gathers the metadata lazily on first access to `__members__` using the same reflection already employed when invoking
  members.  Results may be cached for subsequent calls.
- Overloaded methods produce a separate descriptor for each unique signature.
- The enumeration excludes `__members__` itself and any private members not surfaced by `ToDynamicObject`.
- Future extensions may add attributes (e.g., `IsStatic`, `IsGeneric`) or emit a nested `TypeDescriptor` for generic parameters.

## Open Questions

- Should nested types or operators be included?
- Is `MemberDescriptor` the correct name, or should a more specialised record be introduced per `MemberKind`?
- Should the property return a dictionary keyed by `Name` to ease lookups?

## Summary

`__members__` enables consumers of `ToDynamicObject()` proxies to explore available members without resorting to reflection.
The proposed `MemberDescriptor` record balances ease of consumption with structured detail, and can evolve over time to expose
additional metadata as needed.
