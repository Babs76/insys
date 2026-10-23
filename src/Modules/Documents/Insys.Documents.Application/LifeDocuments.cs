using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Life.Contracts;
using Insys.ProductFactory.Contracts;

namespace Insys.Documents.Application;

/// <summary>
/// Documents' reaction to Life's policies (process "Life proposal to policy": GenerateDocument, policy schedule), with
/// the template assigned to the product. A failure is retried and then dead-lettered; the policy is never rolled back.
/// </summary>
public sealed class LifeDocumentPolicies(DocumentGenerator generator, ISellableProducts products, DocumentLanguages languages, ITenantContext tenant, IClock clock)
    : IIntegrationEventHandler<LifePolicyIssued>
{
    public const string PolicySchedule = "POLICY_SCHEDULE";

    public async Task Handle(LifePolicyIssued issued, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        // In the policyholder's language by the chain (docs/specs/addenda/localisation.md §5).
        var chain = await languages.Chain(issued.PolicyholderPartyId, cancellationToken);
        var template = DocumentLanguages.Pick(await products.Templates(tenant.RequireTenantId(), issued.ProductCode, clock.UtcToday, cancellationToken), PolicySchedule, chain)
            ?? throw new InvalidOperationException($"Product {issued.ProductCode} has no template assigned for {PolicySchedule} in {string.Join(", ", chain)}.");
        var data = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["policy_number"] = issued.PolicyNumber, ["product_code"] = issued.ProductCode, ["product_version"] = issued.ProductVersion,
            ["inception_date"] = issued.InceptionDate.ToString("yyyy-MM-dd"), ["term_end_date"] = issued.TermEndDate?.ToString("yyyy-MM-dd"),
            ["premium"] = issued.Premium, ["frequency"] = issued.BillingFrequency, ["sum_assured"] = issued.SumAssured, ["currency"] = issued.Currency,
        }, JsonSerializerOptions.Web);
        var document = await generator.Generate(new GenerateDocument(template.TemplateCode, template.Language, template.DocumentType, LifeDocuments.PolicyRefType,
            issued.PolicyId, $"{issued.ProductCode} v{issued.ProductVersion}", null, data), cancellationToken);
        if (document.IsFailure)
        {
            throw new InvalidOperationException($"{PolicySchedule} for Life policy {issued.PolicyNumber} was not generated: {document.Error.Message}");
        }
    }
}
