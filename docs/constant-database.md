# Constant database

The constant database provides symbolic names for numeric values encountered
while decompiling.  It can load constants from .NET assemblies or from the
Win32 metadata and exposes two complementary APIs:

- `TryFormatValue` formats a value when the caller already knows the enum that
  should contain it.
- `FindByValue` searches all loaded enums for matches to a numeric value and
  returns every candidate.

## Loading constants

```csharp
var db = new ConstantDatabase();
// Load enums or const classes from a .NET assembly
db.LoadFromAssembly(typeof(ConsoleColor).Assembly);
// Load the Windows SDK metadata if available
// db.LoadWin32MetadataFromWinmd(pathToWinmd);
```

## Mapping call arguments

When a function argument is known to use a specific enum, map it so that
`TryGetArgExpectedEnumType` can later retrieve the enum type.

```csharp
db.MapArgEnum("CreateFileW", 1,
    "Windows.Win32.Storage.FileSystem.FILE_ACCESS_RIGHTS");

if (db.TryGetArgExpectedEnumType("CreateFileW", 1, out var enumName))
{
    // enumName is now the fully qualified name of the expected enum type
}
```

## Formatting when the enum type is known

```csharp
// value is treated as a PROCESS_ACCESS_RIGHTS value
if (db.TryFormatValue(
        "Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS", 0x00100000,
        out var formatted))
{
    // formatted == "Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE"
}
```

## Searching by value

```csharp
// Find every constant whose value is 0x1000
foreach (var match in db.FindByValue(0x1000))
{
    Console.WriteLine(match.Formatted);
}
```

`FindByValue` also synthesises flag combinations when an enum is marked with
`[Flags]` or appears to represent flags.

## Choosing among candidates

`FindByValue` returns all matches without ranking.  Callers are free to apply
their own heuristics—for example, preferring certain namespaces or enum names—to
select the most appropriate symbolic name.

