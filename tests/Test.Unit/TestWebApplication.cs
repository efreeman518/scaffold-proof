using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Test.Unit;

/// <summary>
/// The host project references copy each host's appsettings.json into this test output (the last one built
/// wins), and <see cref="WebApplication.CreateBuilder()"/> also reads the process environment, so a test built on
/// it saw whichever host's config landed there. Tests start from no configuration and add what they assert on.
/// </summary>
internal static class TestWebApplication
{
    public static WebApplicationBuilder CreateBuilder(string? environmentName = null)
    {
        var builder = environmentName is null
            ? WebApplication.CreateBuilder()
            : WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environmentName });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(); // writable source for builder.Configuration[key] = value
        return builder;
    }
}
