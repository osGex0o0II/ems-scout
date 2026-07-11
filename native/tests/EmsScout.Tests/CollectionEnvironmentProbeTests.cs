using EmsScout.Infrastructure.Sidecar;

namespace EmsScout.Tests;

public sealed class CollectionEnvironmentProbeTests
{
    [Theory]
    [InlineData("http://172.29.248.4:8000/ui/#/air", "http://172.29.248.4:8000/ui", true)]
    [InlineData("http://172.29.248.4:8000/other", "http://172.29.248.4:8000/ui", false)]
    [InlineData("http://example.com/ui", "http://172.29.248.4:8000/ui", false)]
    [InlineData("", "http://172.29.248.4:8000/ui", false)]
    public void MatchesOnlyConfiguredEmsPages(string pageUrl, string emsUrl, bool expected)
    {
        Assert.Equal(expected, CollectionEnvironmentProbe.MatchesEmsPage(pageUrl, emsUrl));
    }
}
