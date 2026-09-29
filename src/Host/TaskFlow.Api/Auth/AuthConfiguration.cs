using EF.Auth.Fixed;
using TaskFlow.Application.Contracts;

namespace TaskFlow.Api.Auth;

/// <summary>
/// Config-driven API authentication. TaskFlow supports scaffold mode so the reference app runs
/// with a predictable identity and no interactive or external identity-provider dependency.
/// </summary>
public static class AuthConfiguration
{
    /// <summary>
    /// Registers the EF.Auth fixed-principal scheme with <see cref="ScaffoldPrincipal"/> after validating AuthMode.
    /// The host fails to start outside <see cref="ScaffoldPrincipal.AllowedEnvironments"/>.
    /// </summary>
    public static IServiceCollection AddTaskFlowAuth(this IServiceCollection services, IConfiguration config)
    {
        _ = AuthModeResolver.Resolve(config[AuthModeResolver.ConfigKey]);

        services.AddAuthentication(ScaffoldPrincipal.SchemeName)
            .AddFixedPrincipal(ScaffoldPrincipal.SchemeName, ConfigureScaffoldPrincipal);
        return services;
    }

    /// <summary>The <see cref="ScaffoldPrincipal"/> claims and allowed environments.</summary>
    private static void ConfigureScaffoldPrincipal(FixedPrincipalOptions options)
    {
        options.Claims = [.. ScaffoldPrincipal.Claims.Select(c => new FixedClaim(c.Type, c.Value))];
        options.AllowedEnvironments = [.. ScaffoldPrincipal.AllowedEnvironments];
    }
}
