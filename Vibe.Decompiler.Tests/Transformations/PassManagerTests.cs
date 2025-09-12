using System.Collections.Generic;
using Vibe.Decompiler.Transformations;
using Xunit;

namespace Vibe.Decompiler.Tests.Transformations;

/// <summary>
/// Tests for <see cref="PassManager"/> verifying that passes execute in the
/// order they are registered and that the constructor can accept an initial
/// pass set.
/// </summary>
public class PassManagerTests
{
    /// <summary>
    /// Simple pass implementation that records the execution order by
    /// appending its identifier to a shared list.
    /// </summary>
    private sealed class TrackingPass : ITransformationPass
    {
        private readonly int _id;
        private readonly List<int> _order;

        public TrackingPass(int id, List<int> order)
        {
            _id = id; _order = order;
        }

        /// <inheritdoc />
        public void Run(IR.FunctionIR fn) => _order.Add(_id);
    }

    /// <summary>
    /// Passes should execute in the order they are added to the manager.
    /// </summary>
    [Fact]
    public void RunsPassesInRegistrationOrder()
    {
        var fn = new IR.FunctionIR("test");
        var order = new List<int>();
        var pm = new PassManager();
        pm.Add(new TrackingPass(1, order))
          .Add(new TrackingPass(2, order));
        pm.Run(fn);
        Assert.Equal(new[] { 1, 2 }, order);
    }

    /// <summary>
    /// The constructor allows seeding the manager with an initial set of
    /// passes which are then executed in order.
    /// </summary>
    [Fact]
    public void ConstructorAddsInitialPasses()
    {
        var fn = new IR.FunctionIR("test");
        var order = new List<int>();
        var pm = new PassManager(new ITransformationPass[]
        {
            new TrackingPass(1, order),
            new TrackingPass(2, order)
        });
        pm.Run(fn);
        Assert.Equal(new[] { 1, 2 }, order);
    }
}
