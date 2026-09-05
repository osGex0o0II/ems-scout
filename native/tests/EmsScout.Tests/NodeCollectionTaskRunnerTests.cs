using EmsScout.Application.Collection;

namespace EmsScout.Tests;

public sealed class NodeCollectionTaskRunnerTests
{
    [Fact]
    public void RejectsRelativeNodeRuntimeOverride()
    {
        var previous = Environment.GetEnvironmentVariable("EMS_NODE_RUNTIME");
        try
        {
            Environment.SetEnvironmentVariable("EMS_NODE_RUNTIME", "node.exe");
            Assert.Throws<InvalidOperationException>(() => NodeRuntimeResolver.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable("EMS_NODE_RUNTIME", previous);
        }
    }
}
