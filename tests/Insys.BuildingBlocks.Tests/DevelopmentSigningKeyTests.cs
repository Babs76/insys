using Insys.BuildingBlocks.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Tests;

/// <summary>The development signing key is used only in the allow-listed environments, Development and Testing (security fix, Phase 8).</summary>
public sealed class DevelopmentSigningKeyTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    [InlineData("Staging", false)]
    [InlineData("Production", false)]
    [InlineData("development", false)]
    public async Task DevelopmentKey_ValidatesTokens_OnlyInTheAllowList_AndElsewhereStartupFails(string environment, bool allowed)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Issuer"] = "insys-local", ["Authentication:Audience"] = "insys-core-api",
            ["Authentication:DevelopmentSigningKey"] = "a-development-signing-key-for-this-test-0123456789",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new Environment(environment));
        services.AddLogging();
        services.AddInsysApi();
        await using var provider = services.BuildServiceProvider();

        var bearer = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        (bearer.TokenValidationParameters.IssuerSigningKey is not null).ShouldBe(allowed);

        var guard = ActivatorUtilities.CreateInstance<DevelopmentSigningKeyGuard>(provider);
        if (allowed)
        {
            await guard.StartAsync(CancellationToken.None);
        }
        else
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => guard.StartAsync(CancellationToken.None))).Message.ShouldContain(environment);
        }
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Insys.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
