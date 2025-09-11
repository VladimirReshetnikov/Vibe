# Transformation Architecture Refactor Plan

The existing transformation framework grew organically and now bundles all rewrite logic into a single static class. This document lays out a detailed, actionable plan for restructuring the system into a modular and extensible architecture. The goal is to make individual passes easier to write, easier to test, and simpler to compose into flexible pipelines.

## 1. Current Structure

- File: `Vibe.Decompiler/Transformations.cs`
- Type: `internal static class Transformations`
- Responsibilities:
  - Provides helper traversal methods: `RewriteStmt` and `RewriteExpr`.
  - Implements every transformation pass as a static method on the class.
  - Callers manually invoke specific transformation methods in ad-hoc sequences.
- Characteristics:
  - ~35 lines of traversal helpers precede the first pass implementation.
  - Each pass defines nested local functions for recursion.
  - No explicit mechanism to express pass dependencies or configuration options.

## 2. Pain Points and Motivations

1. **Monolithic file** – All passes share one file and one static type. The file is already long and will become harder to navigate as new passes are added.
2. **Duplicated traversal logic** – Every pass declares local functions that perform similar tree walks, producing boilerplate and risking inconsistent behavior.
3. **Implicit ordering** – Without a pipeline, consumers must remember the correct pass order; mistakes surface as subtle bugs.
4. **Limited configurability** – Passes cannot accept options or contextual services because they are static methods.
5. **Difficult testing** – Individual passes cannot easily be isolated for unit tests because they are implemented as internal static helpers.
6. **Opaque dependencies** – There is no place to declare "pass A requires pass B" or "pass C must run after pass D".
7. **Poor discoverability** – New contributors must read a long list of methods to find the pass they need to modify.
8. **Long conditional chains** – Pattern matching relies on sequences of `if`/`else if` statements, hindering maintainability.

## 3. Goals

- Introduce explicit abstractions for passes and pass management.
- Remove duplicated traversal code by providing a reusable visitor base.
- Allow passes to declare dependencies and configuration options.
- Ensure each pass resides in its own file with clear naming conventions.
- Provide comprehensive unit and integration tests for each pass and for the pipeline.
- Enable future passes to be added without modifying existing files or centralized switch statements.
- Maintain current behavior during the migration to avoid regressions.

## 4. Target Architecture Overview

The refactored system introduces three foundational components: a pass interface, a pass manager, and a set of reusable IR rewriting utilities.

### 4.1 Pass Interface

```csharp
namespace Vibe.Decompiler.Transformations
{
    public interface ITransformationPass
    {
        /// <summary>
        /// Executes the transformation on the provided function.
        /// </summary>
        /// <param name="fn">Function IR to mutate in-place.</param>
        void Run(IR.FunctionIR fn);
    }
}
```

- Every transformation becomes a class implementing `ITransformationPass`.
- Naming convention: `*Pass` suffix, e.g., `SimplifyArithmeticPass`.
- Passes may expose configuration via constructor parameters.
- Each pass is responsible only for its specific transformation, not for orchestration.

### 4.2 Pass Manager

```csharp
public sealed class PassManager
{
    private readonly List<ITransformationPass> _passes = new();

    public PassManager(IEnumerable<ITransformationPass> passes)
    {
        _passes.AddRange(passes);
    }

    public void Run(IR.FunctionIR fn)
    {
        foreach (var pass in _passes)
        {
            pass.Run(fn);
        }
    }
}
```

- Maintains an ordered list of passes.
- Receives the pass order via constructor or fluent `Add` methods.
- Can expose validation hooks to ensure required dependencies are present.
- Future extension: accept `IPassContext` parameter for caching or logging.

### 4.3 IR Rewriter Utilities

- Introduce an `IRRewriter` base class in `Vibe.Decompiler/Transformations/IRRewriter.cs`.
- Design mirrors a visitor pattern with virtual methods for each IR node type.
- Passes subclass `IRRewriter` and override only the nodes they intend to modify.
- Provides `Rewrite` methods to traverse statements and expressions while allowing node replacement.

Example skeleton:

```csharp
public abstract class IRRewriter
{
    public virtual IR.Stmt RewriteStmt(IR.Stmt stmt) => stmt switch
    {
        IR.BlockStmt block => RewriteBlock(block),
        IR.AssignStmt assign => RewriteAssign(assign),
        _ => stmt,
    };

    protected virtual IR.Stmt RewriteBlock(IR.BlockStmt block)
    {
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[i] = RewriteStmt(block.Statements[i]);
        }
        return block;
    }

    protected virtual IR.Stmt RewriteAssign(IR.AssignStmt assign) => assign;
}
```

### 4.4 Rule-Based Pattern Engine

- Replace long conditional chains with a declarative rule engine.
- Store rewrite rules in a `Dictionary<(OpKind, Type, Type), Func<IR.Expr, IR.Expr>>`.
- Each entry maps a pattern to a replacement function.
- Passes populate the dictionary in their constructor and leverage it during rewriting.
- Enables adding or disabling rules without altering control flow logic.

### 4.5 File Organization

- Directory: `Vibe.Decompiler/Transformations/`
  - `ITransformationPass.cs`
  - `PassManager.cs`
  - `IRRewriter.cs`
  - `SimplifyArithmeticPass.cs`
  - `FoldConstantsPass.cs`
  - Additional passes as separate files.
- Tests: `Vibe.Decompiler.Tests/Transformations/` with mirrored structure.
- Each pass file contains XML doc comments describing purpose and known limitations.

## 5. Detailed Implementation Steps

1. **Create foundational interfaces and utilities.**
   - Add `ITransformationPass` and `PassManager` classes.
   - Introduce `IRRewriter` with basic traversal for statements and expressions.
   - Confirm existing transformations compile against the new infrastructure.
2. **Migrate existing passes one by one.**
   - For each static method in `Transformations`, create a corresponding `*Pass` class.
   - Replace local traversal functions with overrides on `IRRewriter`.
   - Add unit tests that exercise the pass on small IR fragments.
3. **Assemble the default pipeline.**
   - Identify the current ordering used by consumers.
   - Build a `DefaultPassPipeline` static class that exposes a factory method returning a `PassManager` configured with the standard passes.
   - Update call sites to obtain and execute this pipeline instead of manually invoking passes.
4. **Introduce configuration options.**
   - Example: `ConstantFoldingPass(bool treatOverflowAsError)`.
   - Pass manager should accept parameters or dependency injection to supply options.
5. **Remove obsolete `Transformations` class.**
   - After all passes migrate, delete the original static class.
   - Provide a compatibility shim during transition if needed.
6. **Enhance pattern matching.**
   - Implement reusable `RewriteRule` records: `record RewriteRule(OpKind Op, string LeftType, string RightType, Func<IR.Expr, IR.Expr> Replace);`
   - Allow passes to compose rule lists and execute them via `RuleEngine.Apply(expr, rules)`.
7. **Documentation and Examples.**
   - Update README and examples to demonstrate building and running a pass pipeline.
   - Provide inline XML documentation for each public API.

## 6. Testing Strategy

- **Unit tests for passes** – For each pass, create test cases that feed a small IR snippet, run the pass, and assert on the resulting IR tree.
- **Integration tests for pipelines** – Build a sample pipeline mirroring the expected production order and verify it compiles representative functions correctly.
- **Regression tests** – When migrating a pass, port any existing test cases and add new ones to cover previously untested edge cases.
- **Performance benchmarks** – Add optional benchmark tests using BenchmarkDotNet to ensure the new architecture does not introduce significant overhead.
- **Continuous Integration hooks** – Configure the repository to run all pass tests and pipeline tests on each commit.

## 7. Migration Checklist

 - [x] Create foundational files (`ITransformationPass`, `PassManager`, `IRRewriter`).
- [x] Move `SimplifyRedundantAssign` into `SimplifyRedundantAssignPass`.
- [x] Add unit tests for `SimplifyRedundantAssignPass`.
 - [x] Move `SimplifyArithmeticIdentities` into `SimplifyArithmeticPass`.
 - [x] Add unit tests for `SimplifyArithmeticPass`.
 - [x] Move `FoldConstants` into `FoldConstantsPass`.
 - [x] Add unit tests for `FoldConstantsPass`.
 - [ ] Repeat for remaining passes: `DeadCodeEliminationPass`, `CanonicalizeLoopsPass`, etc.
 - [x] Create `DefaultPassPipeline` with pass ordering derived from current usage.
 - [x] Update callers to use `DefaultPassPipeline.Create()`.
 - [ ] Remove `Transformations.cs`.
 - [ ] Ensure documentation and comments reference new architecture.

## 8. Risk Mitigation and Rollout Plan

- **Incremental commits** – Migrate one pass at a time to keep diffs reviewable.
- **Feature flags** – If necessary, introduce a feature flag allowing consumers to toggle between the old static methods and the new pipeline during transition.
- **Fallback path** – Maintain the old `Transformations` class until all passes are migrated and validated.
- **Comprehensive testing** – Run unit and integration tests after each migration to catch regressions early.
- **Code reviews** – Require at least one reviewer to verify adherence to the new conventions for each pass migration.

## 9. Example Usage After Refactor

```csharp
var pipeline = DefaultPassPipeline.Create();
foreach (var fn in module.Functions)
{
    pipeline.Run(fn);
}
```

- `DefaultPassPipeline.Create()` constructs a `PassManager` loaded with the standard passes in the correct order.
- Consumers can build custom pipelines by instantiating `PassManager` with a different pass list.

## 10. Future Extensions

- **Parallel Pass Execution** – Explore running independent passes in parallel once thread-safety is verified.
- **Pass Metadata** – Attach descriptive metadata (author, version, description) to each pass for better diagnostics.
- **Diagnostics and Logging** – Extend `PassManager` to emit logs or diagnostics per pass, aiding debugging and performance tracking.
- **Undo Support** – Provide an optional mechanism to rollback transformations, useful during interactive development.

## 11. Conclusion

By decomposing the monolithic `Transformations` class into discrete passes managed by a pipeline, the Vibe project gains flexibility, testability, and clarity. The steps above provide a concrete roadmap with clear milestones, ensuring the migration proceeds in a controlled and verifiable manner.
