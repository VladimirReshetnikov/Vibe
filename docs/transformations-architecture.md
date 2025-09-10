# Transformation Architecture Review

## Current Structure
- `Transformations` is a single static class containing helper rewrite functions and every transformation pass in one file.
- `RewriteStmt`/`RewriteExpr` perform generic IR traversal and are defined at the top of the class.

## Observations
1. **Monolithic class** – All passes sit in `Transformations.cs`. The helper traversal functions and the first pass already occupy ~35 lines before any specific logic begins.
2. **Repeated boilerplate** – Each method defines local `Rewriter` functions and switch statements to walk the tree, duplicating traversal logic.
3. **Implicit ordering and dependencies** – There is no pass interface or pipeline; callers invoke methods manually, so the order and dependencies are implicit.
4. **Long conditional chains** – Pattern matching is expressed through long `if`/`else if` sequences (e.g., `SimplifyArithmeticIdentities` spans many branches to cover operators).

## Proposed Refactor
1. **Introduce a pass interface**
   ```csharp
   public interface ITransformationPass
   {
       void Run(IR.FunctionIR fn);
   }
   ```
   - Move each transformation into its own class implementing this interface.
2. **Create a pass manager**
   - Maintain an ordered list of `ITransformationPass` instances.
   - Allow configuration via constructor parameters or dependency injection.
   - Support dependency declarations so the manager can verify ordering.
3. **Shared IR rewriting utilities**
   - Extract the generic traversal (`RewriteStmt`/`RewriteExpr`) into an `IRRewriter` base class that exposes overridable visit methods.
   - Passes override only the nodes they care about, reducing boilerplate.
4. **Rule-based pattern engine**
   - Replace long `if` chains with a table of patterns and replacements (e.g., dictionaries keyed by operator).
   - Enables declarative addition of new rules without touching existing ones.
5. **File organization**
   - Each pass resides in `Vibe.Decompiler/Transformations/` folder, one file per pass.
   - Unit tests cover individual passes to prevent regressions.

## Implementation Steps
1. Add `ITransformationPass` and `PassManager` types.
2. Gradually migrate existing methods into dedicated pass classes.
3. Introduce `IRRewriter` base to remove duplicate traversal code.
4. Replace conditional chains with rule tables or pattern objects.
5. Update call sites to use the pass manager and ensure tests build the pipeline.

## Expected Benefits
- Clear separation of concerns and easier maintenance.
- Independent passes with well-defined dependencies and configuration.
- Simplified extension of the transformation pipeline without touching a monolithic file.
