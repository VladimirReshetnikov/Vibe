using Vibe.Decompiler;

namespace Vibe.Decompiler.Transformations;

/// <summary>
/// Represents a transformation pass that mutates a function IR in-place.
/// </summary>
public interface ITransformationPass
{
    /// <summary>Execute the transformation on the provided function.</summary>
    /// <param name="fn">Function IR to mutate.</param>
    void Run(IR.FunctionIR fn);
}
