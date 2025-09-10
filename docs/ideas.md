# Transformation Pass Ideas

A few small transformation passes that could be implemented with modest effort:

- **Constant Folding** – Evaluate constant expressions at compile time.
- **Dead Code Elimination** – Remove instructions that never affect program output.
- **Copy Propagation** – Replace occurrences of variables with their known values when safe.
- **Algebraic Simplification** – Apply simple algebraic identities to simplify expressions.
- **Inline Expansion** – Substitute small functions directly at call sites to reduce call overhead.

These passes can provide immediate benefits while requiring relatively little infrastructure.
