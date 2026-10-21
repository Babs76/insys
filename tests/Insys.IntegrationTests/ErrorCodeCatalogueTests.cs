using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// The error code catalogue has one source: the [ErrorCode] constants in code (item 8). The repeatable seed is
/// generated from them, and the database catalogue matches. To accept new codes, run with INSYS_UPDATE_SNAPSHOTS=1.
/// </summary>
public sealed class ErrorCodeCatalogueTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task SeedScript_IsGeneratedFromTheCodeConstants()
    {
        var expected = ErrorCodeSeed.Generate();
        if (Environment.GetEnvironmentVariable("INSYS_UPDATE_SNAPSHOTS") == "1")
        {
            await File.WriteAllTextAsync(ErrorCodeSeed.FilePath, expected);
        }

        File.Exists(ErrorCodeSeed.FilePath).ShouldBeTrue("Run the tests once with INSYS_UPDATE_SNAPSHOTS=1 to generate the seed.");
        (await File.ReadAllTextAsync(ErrorCodeSeed.FilePath)).ShouldBe(expected,
            "Error codes in code changed. Run with INSYS_UPDATE_SNAPSHOTS=1 and commit db/migrations/R__error_code_catalogue.sql.");
    }

    [Fact]
    public async Task DatabaseCatalogue_HasEveryCode_WithTheSameStatusAndRetryFlag()
    {
        var mismatches = new List<string>();
        foreach (var entry in ErrorCodeSeed.Entries())
        {
            var row = await fixture.Database.ScalarAsOwner<string>("""
                SELECT http_status || '|' || retryable || '|' || message_key FROM integration.ref_error_code_catalogue
                WHERE tenant_id IS NULL AND error_code = @code AND record_status = 'ACTIVE'
                """, ("code", entry.Code));
            var expected = $"{entry.HttpStatus}|{(entry.Retryable ? "true" : "false")}|{entry.MessageKey}";
            if (row != expected)
            {
                mismatches.Add($"{entry.Code}: database '{row}', code '{expected}'");
            }
        }

        mismatches.ShouldBeEmpty();
    }
}
