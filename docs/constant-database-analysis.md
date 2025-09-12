# Constant database: current state and redesign plan

## Overview
`Vibe.Decompiler/ConstantDatabase.cs` acts as the default implementation of
`IConstantNameProvider`.  It builds a dictionary of enums and exposes
`TryFormatValue` which formats a number when the caller already knows the
fully qualified enum name to check.

## Current implementation
- `IConstantNameProvider` only exposes `TryFormatValue(string enumFullName, ulong value, out string name)`
  meaning the caller must already know which enum to use.
- `ConstantDatabase` keeps a map of enum type names to metadata and a separate
  map for expected enums for specific call arguments.
- `TryGetArgExpectedEnumType` returns a mapped enum for a function argument,
  but it only succeeds when the symbol name and index were mapped during
  construction and the enum is already loaded.
- `TryFormatValue` looks up the enum and either matches an exact value or
  tries to decompose the value into known flag parts.  If no match is found
  it returns a hexadecimal string.

## How the decompiler uses it
`Engine` passes the configured constant provider and a fixed return enum type
into two legacy transformations.  `MapNamedReturnConstants` and
`MapNamedRetAssignConstants` only replace immediates with symbolic names when
the enum type is known ahead of time.

The decompiler currently has no mechanism to ask for all constants matching a
value.  `TryGetArgExpectedEnumType` is intended for argument rendering but is
not used anywhere yet.

## Why this design fails
The failing test `LooksUpWinApiConstantsByHexValue` illustrates the problem:
`TryFormatValue` requires a fully qualified enum name, but the decompiler's
actual need is to start with a numeric value and discover any plausible
constants across all loaded enums.  Requiring the caller to provide the enum
name defeats the purpose; the decompiler rarely knows which enum a value
belongs to.

## Plan for a correct implementation
1. **New API** – extend `IConstantNameProvider` with a method such as
   `IEnumerable<ConstantMatch> FindByValue(ulong value, int bitWidth = 32)`
   that returns all matching constants, including the enum name and the
   formatted string.
2. **Global index** – while loading enums, build a value‑centric index
   `Dictionary<ulong, List<ConstantMatch>>` for exact values.  For flag enums,
   store single‑bit members so candidates can be synthesized at query time.
3. **Search logic** – when `FindByValue` is called, look up exact matches in
   the global index and, for flag enums, attempt to compose flag combinations
   similar to the current `TryFormatValue` logic.
4. **Heuristics** – return all candidates and let the caller choose.  Future
   heuristics could rank matches by namespace, enum name, or flag completeness.
5. **Decompiler integration** – modify existing transformations to call
   `FindByValue` instead of `TryFormatValue`.  When a specific enum is known
   (e.g., return type or mapped argument), filter the results accordingly;
   otherwise apply heuristics to select a single symbolic name or keep the
   numeric literal if ambiguous.
6. **Tests** – replace tests that require an explicit enum name with tests
   that verify searching by value across all enums and evaluate the heuristic
   behavior.

This redesign decouples the constant lookup from specific enum types and aligns
with the decompiler's need to start with raw numbers and infer meaningful
symbolic names.
