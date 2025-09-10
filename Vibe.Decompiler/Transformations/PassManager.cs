using System.Collections.Generic;

namespace Vibe.Decompiler.Transformations;

/// <summary>
/// Orchestrates execution of a sequence of transformation passes.
/// </summary>
public sealed class PassManager
{
    private readonly List<ITransformationPass> _passes = new();

    public PassManager()
    {
    }

    public PassManager(IEnumerable<ITransformationPass> passes)
    {
        _passes.AddRange(passes);
    }

    /// <summary>Adds a pass to the pipeline.</summary>
    public PassManager Add(ITransformationPass pass)
    {
        _passes.Add(pass);
        return this;
    }

    /// <summary>Runs all passes in order on the provided function.</summary>
    public void Run(IR.FunctionIR fn)
    {
        foreach (var pass in _passes)
            pass.Run(fn);
    }
}
