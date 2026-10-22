using System.Net;
using Insys.BuildingBlocks.Api.StaticData;
using Insys.ProductFactory.Contracts;
using Insys.ProductFactory.Domain.Products;
using Insys.ProductFactory.Infrastructure;
using Insys.Rating.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.EndToEndTests;

/// <summary>
/// Process "Product publication" (domain-model.json; §27.3 scenario 1; PRD-001 to PRD-010), through the API with the
/// real migrations, including its compensations: validation errors keep the version in Draft, a failed simulation case
/// blocks submission, and a rejected approval returns the version to Draft.
/// </summary>
public sealed class ProductPublicationTests(PostgresServer server) : IAsyncLifetime
{
    private const string Product = "E2E-MOTOR";
    private const string Versions = $"/api/v1/product-factory/products/{Product}/versions";
    private static readonly Guid Tenant = TestTenants.A;

    private TestDatabase _db = null!;
    private InsysApiFactory _factory = null!;
    private Guid _checker;

    public async ValueTask InitializeAsync()
    {
        _db = await server.CreateMigratedDatabase();
        await _db.SeedTenant(Tenant, "TENA");
        string[] permissions =
        [
            ProductPermissions.ProductRead, ProductPermissions.ProductCreate, ProductPermissions.ProductUpdate, ProductPermissions.ProductPublish,
            WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove, RatingPermissions.CalculationCreate, RatingPermissions.CalculationRead,
            StaticDataPermissions.Read,
        ];
        await _db.UserWithPermissions(Tenant, "product-manager", permissions);
        _checker = await _db.UserWithPermissions(Tenant, "senior-actuary", permissions);
        var approverRole = await _db.RoleWithPermissions(Tenant);
        await _db.AssignRole(Tenant, _checker, approverRole);

        await Static("workflow.cfg_delegation_and_approval_authority", "AUTH-PUB", "role_code, transaction_type, limit_value, delegation_allowed",
            $"'{approverRole}', '{ProductApprovals.ApprovalType}', 0, false");
        await Static("product.ref_product_taxonomy", "GEN-MOTOR", "line, family_code, family_name", "'GENERAL', 'GEN-MOTOR', 'Motor insurance'", reference: true);
        await Static("product.cfg_coverage_and_benefit_library", "OWN_DAMAGE", "type, name, basis, measure, domain", "'COVERAGE', 'Own damage', 'INDEMNITY', 'SUM_INSURED', 'GENERAL'");
        await Static("product.cfg_coverage_and_benefit_library", "THEFT", "type, name, basis, measure, domain", "'COVERAGE', 'Theft', 'INDEMNITY', 'SUM_INSURED', 'GENERAL'");
        await Static("product.cfg_benefit_dependency_rules", "THEFT-NEEDS-OD", "benefit, relation, target_benefit", "'THEFT', 'REQUIRES', 'OWN_DAMAGE'");
        await Static("rating.cfg_rating_factor_definitions", "VEHICLE_GROUP", "factor_code, data_type, source_attribute", "'VEHICLE_GROUP', 'CODE', 'vehicle_group'");
        await Static("rating.cfg_rating_tables", "MOT-BASE", "table_code, dimensions, values, interpolation",
            """'MOT-BASE', ARRAY['VEHICLE_GROUP'], '[{"key":["G3"],"value":0.03}]', 'NONE'""");
        await Static("rating.cfg_premium_parameters", "E2E-BASE", "parameter_code, product_version, value, unit", $"'MOT-BASE', '{Product} v1', 1, 'FACTOR'");
        await Static("rating.cfg_premium_parameters", "E2E-FEE", "parameter_code, product_version, value, unit", $"'POLICY_FEE', '{Product} v1', 50, 'AMOUNT'");
        await Static("rating.cfg_rounding_and_currency_precision_rules", "ZAR-2DP", "rule_code, currency, step, method, stage", "'ZAR-2DP', 'ZAR', 0.01, 'HALF_UP', 'FINAL_PREMIUM'");

        // E2E-TC-1 is an engineering case (docs/rating/test-cases.md), not an actuary-supplied one. It is wrong at first (3 000.00 instead of 100 000 × 0.03 + 50 = 3 050.00).
        await Static("rating.cfg_rating_test_case_library", "E2E-TC-1", "case_id, product_version, inputs, expected_premium, tolerance",
            $$"""'E2E-TC-1', '{{Product}} v1', '{"currency":"ZAR","frequency":"MONTHLY","sum_insured":100000,"vehicle_group":"G3"}', 3000, 0.01""");

        _factory = new InsysApiFactory(_db);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task ProductPublication_FromDraftToEffective_WithItsCompensations()
    {
        var maker = _factory.ClientFor(Tenant, "product-manager");
        var checker = _factory.ClientFor(Tenant, "senior-actuary");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var v1 = $"{Versions}/1";

        // CreateDraftVersion (RegisterProduct for a new code) and EditConfiguration.
        var registered = await maker.Send(HttpMethod.Post, "/api/v1/product-factory/products", new
        {
            productCode = Product, domain = "General", familyCode = "GEN-MOTOR", name = "Motor comprehensive", coverages = new[] { "THEFT" }, effectiveFrom = today,
        }, idempotent: true);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Status(maker, v1)).ShouldBe("Draft");

        // Compensation: validation errors keep the version in Draft.
        var invalid = await (await maker.Send(HttpMethod.Post, $"{v1}/validate", ifMatch: "\"1\"")).Json();
        invalid.GetProperty("code").GetString().ShouldBe(ProductErrorCodes.ValidationFailed);
        invalid.GetProperty("detail").GetString()!.ShouldContain("THEFT requires OWN_DAMAGE");
        (await Status(maker, v1)).ShouldBe("Draft");

        (await maker.Send(HttpMethod.Put, v1, new { name = "Motor comprehensive", coverages = new[] { "OWN_DAMAGE", "THEFT" }, effectiveFrom = today }, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await maker.Send(HttpMethod.Post, $"{v1}/validate", ifMatch: "\"2\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Status(maker, v1)).ShouldBe("Validated");
        await _factory.DrainOutbox();
        (await Events("product", "product.ProductVersionValidated")).ShouldBe(1);

        // RunSimulation against approved test cases. Compensation: any failed case blocks submission.
        var failedRun = await (await maker.Send(HttpMethod.Post, $"{v1}/simulations", idempotent: true)).Json();
        failedRun.GetProperty("status").GetString().ShouldBe("Failed");
        failedRun.GetProperty("results")[0].GetProperty("actual").GetProperty("premium").GetDecimal().ShouldBe(3050m);
        var blocked = await maker.Send(HttpMethod.Post, $"{v1}/submit", new { reason = "Launch" }, ifMatch: "\"3\"");
        (await blocked.Code()).ShouldBe(ProductErrorCodes.SimulationRequired);

        // Approved test cases are immutable (SDM-002): the corrected case is version 2, in force from today.
        await _db.ExecuteAsOwner("UPDATE rating.cfg_rating_test_case_library SET effective_to = current_date WHERE code = 'E2E-TC-1'");
        await _db.ExecuteAsOwner($$"""
            INSERT INTO rating.cfg_rating_test_case_library (tenant_id, code, version, effective_from, record_status, created_by, change_reason,
                                                             case_id, product_version, inputs, expected_premium, tolerance)
            VALUES (@t, 'E2E-TC-1', 2, current_date, 'EFFECTIVE', 'seed', 'corrected', 'E2E-TC-1', '{{Product}} v1',
                    '{"currency":"ZAR","frequency":"MONTHLY","sum_insured":100000,"vehicle_group":"G3"}', 3050, 0.01)
            """, ("t", Tenant));
        (await (await maker.Send(HttpMethod.Post, $"{v1}/simulations", idempotent: true)).Json()).GetProperty("status").GetString().ShouldBe("Passed");

        // SubmitForApproval: Workflow is asked for a PRODUCT_PUBLICATION approval. Compensation: a rejected approval returns the version to Draft.
        (await maker.Send(HttpMethod.Post, $"{v1}/submit", new { reason = "Launch" }, ifMatch: "\"3\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        var approval = await Approval(checker);
        (await checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/reject", new { reason = "Check the fee" }, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        (await Status(maker, v1)).ShouldBe("Draft");

        // Again, and this time the checker approves: maker and checker differ (SRS-002, PRD-006).
        (await maker.Send(HttpMethod.Post, $"{v1}/validate", ifMatch: "\"5\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var stale = await maker.Send(HttpMethod.Post, $"{v1}/submit", new { reason = "Launch" }, ifMatch: "\"6\"");
        (await stale.Code()).ShouldBe(ProductErrorCodes.SimulationRequired);
        (await (await maker.Send(HttpMethod.Post, $"{v1}/simulations", idempotent: true)).Json()).GetProperty("status").GetString().ShouldBe("Passed");
        (await maker.Send(HttpMethod.Post, $"{v1}/submit", new { reason = "Launch" }, ifMatch: "\"6\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        approval = await Approval(checker);
        var selfApproval = await maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"");
        (await selfApproval.Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // ApprovalGranted → ProductVersionApproved → PublishVersion → ProductVersionPublished → Rating loads the new version.
        await _factory.DrainOutbox();
        var published = await (await maker.GetAsync(v1)).Json();
        published.GetProperty("status").GetString().ShouldBe("Published");
        published.GetProperty("approvedBy").GetString().ShouldBe(_checker.ToString());
        (await Events("product", "product.ProductVersionApproved")).ShouldBe(1);
        (await Events("product", "product.ProductVersionPublished")).ShouldBe(1);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM rating.inbox_message WHERE event_type = 'product.ProductVersionPublished'")).ShouldBe(1);
        (await maker.Send(HttpMethod.Put, v1, new { name = "Changed", coverages = new[] { "OWN_DAMAGE" }, effectiveFrom = today }, ifMatch: "\"8\""))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // The published version rates, and the calculation records the product and rating versions (PRD-008).
        var calculation = await (await maker.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
        {
            requestRef = "E2E-Q1", sourceDomain = "GENERAL", subjectType = "QUOTE", subjectId = Guid.CreateVersion7(), productCode = Product, productVersion = 1,
            currency = "ZAR", frequency = "MONTHLY", values = new { sum_insured = 100000, vehicle_group = "G3" },
        }, idempotent: true)).Json();
        calculation.GetProperty("premium").GetDecimal().ShouldBe(3050m);
        calculation.GetProperty("ratingVersion").GetInt32().ShouldBe(1);

        // Effective date reached. TODO(spec): "Refresh availability" in Life, General and Health comes with those domains (Phase 4/5).
        (await _factory.Services.GetRequiredService<ProductJobs>().Run(CancellationToken.None)).ShouldBe(1);
        (await Status(maker, v1)).ShouldBe("Effective");
        (await (await maker.GetAsync($"/api/v1/product-factory/static/product-definitions-and-versions/{Product}")).Json())
            .GetProperty("recordStatus").GetString().ShouldBe("EFFECTIVE");
    }

    private static async Task<string?> Status(HttpClient client, string url) => (await (await client.GetAsync(url)).Json()).GetProperty("status").GetString();

    private async Task<Guid> Approval(HttpClient client)
    {
        var versionId = await _db.ScalarAsOwner<Guid>("SELECT id FROM product.cfg_product_definitions_and_versions WHERE code = @c AND version = 1", ("c", Product));
        var page = await (await client.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType={ProductApprovals.BusinessRefType}&businessRefId={versionId}"))
            .Json();
        var item = page.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("approvalType").GetString().ShouldBe(ProductApprovals.ApprovalType);
        return item.GetProperty("id").GetGuid();
    }

    private Task<long> Events(string schema, string type) =>
        _db.ScalarAsOwner<long>($"SELECT count(*) FROM {schema}.outbox_event WHERE event_type = @t", ("t", type));

    private Task Static(string table, string code, string columns, string values, bool reference = false) =>
        _db.ExecuteAsOwner($"""
            INSERT INTO {table} (tenant_id, code, version, effective_from, record_status, created_by, change_reason, {columns})
            VALUES (@t, @code, 1, current_date - 1, '{(reference ? "ACTIVE" : "EFFECTIVE")}', 'seed', 'test seed', {values})
            """, ("t", Tenant), ("code", code));
}
