namespace Vibe.Decompiler.Transformations;

/// <summary>
/// Provides a factory for the default transformation pipeline.
/// </summary>
public static class DefaultPassPipeline
{
    /// <summary>
    /// Creates a pass manager configured with the standard set of passes.
    /// </summary>
    /// <returns>A pass manager ready to run the default pipeline.</returns>
    public static PassManager Create()
    {
        return new PassManager(new ITransformationPass[]
        {
            new SimplifyRedundantAssignPass(),
            new FoldConstantsPass(),
            new SimplifyArithmeticPass(),
            new SimplifyLogicalNotsPass(),
            new SimplifyBooleanTernaryPass(),
        });
    }
}
