# Coding Guidelines

The following guidelines summarize coding style preferences demonstrated in commit 7d7010adb360ae8926ebe54eb0e16bc23baae43f.

## General
- Use `var` for local variables when the type is obvious from the right-hand side.
- Prefer early returns and guard clauses over deeply nested conditionals.
- Keep private fields prefixed with an underscore (`_field`).
- Indent with four spaces; use PascalCase for types and methods, camelCase for locals.

## Modern C# Features
- Favor expression-bodied members for simple methods or property getters.
- Use switch expressions and pattern matching, including property, relational and `or` patterns.
- Apply `is`/`is not` patterns for type checks and combine with property patterns when helpful.
- Employ collection expressions (`[ ... ]`) and target-typed `new()` when the type is clear.
- Use range indexing (`s[(i + 6)..]`) instead of `Substring`.

## Conditionals
- Collapse nested `if` statements with early `continue`/`return` when possible.
- Replace `if`/`else` chains with pattern-based switches where appropriate.
- Prefer char overloads and single-character literals (e.g., `StartsWith('p')`, `Append(':')`).
- Use `TryAdd` or other methods that avoid double lookup instead of explicit `ContainsKey` checks.

## String Handling
- Use string interpolation and specify `StringComparison` when comparing strings.
- Prefer `ToLowerInvariant()` for register names or other case transformations.

## Enum and Bitwise Work
- Use object initializers to set properties (`new T { Prop = value }`).
- When evaluating flags, compute lists and return early if the condition is unmet.

These points provide an initial style reference for contributors. Future commits can expand or refine these guidelines.
