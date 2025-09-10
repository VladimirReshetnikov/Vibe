namespace Vibe.Decompiler.Transformations;

/// <summary>
/// Provides a factory for the default transformation pipeline.
/// </summary>
public static class DefaultPassPipeline
{
    /// <summary>
    /// Creates a pass manager configured with the standard set of passes.
    /// </summary>
    public static PassManager Create()
    {
        return new PassManager(new ITransformationPass[]
        {
            new SimplifyRedundantAssignPass(),
        });
    }
}
