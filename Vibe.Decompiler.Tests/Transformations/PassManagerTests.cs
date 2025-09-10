using System.Collections.Generic;
using Vibe.Decompiler.Transformations;
using Xunit;

namespace Vibe.Decompiler.Tests.Transformations;

public class PassManagerTests
{
    private sealed class TrackingPass : ITransformationPass
    {
        private readonly int _id;
        private readonly List<int> _order;
        public TrackingPass(int id, List<int> order)
        {
            _id = id; _order = order;
        }
        public void Run(IR.FunctionIR fn) => _order.Add(_id);
    }

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
