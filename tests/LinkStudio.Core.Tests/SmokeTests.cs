using LinkStudio.Core;
using Xunit;

namespace LinkStudio.Core.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void MetadataHasExpectedAppName()
    {
        Assert.Equal("LinkStudio", LinkStudioMetadata.AppName);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", LinkStudioMetadata.Version);
    }
}
