using System.Globalization;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Health.Contracts;

namespace Insys.Documents.Application;

/// <summary>
/// Documents' reaction to Health's payment runs: a released run's remittance advices, one document per provider, from the
/// remittance advice format's template (health.cfg_remittance_advice_formats.template, "TPL-RA" or "TPL-RA v2"; the
/// template version in force is used). Health attaches each to its remittance on DocumentGenerated. A failure is retried
/// and then dead-lettered; the run is never rolled back.
/// </summary>
public sealed class HealthDocumentPolicies(DocumentGenerator generator, IDocumentCatalogue catalogue, IClock clock) : IIntegrationEventHandler<ProviderPaymentRunReleased>
{
    public async Task Handle(ProviderPaymentRunReleased released, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (released.RemittanceTemplate is not { } configured)
        {
            return;
        }

        var code = configured.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var template = await catalogue.AnyTemplate(code, clock.UtcToday, cancellationToken)
                       ?? throw new InvalidOperationException($"Remittance advice template {code} is not in force; run {released.RunCode}'s advices were not generated.");
        foreach (var remittance in released.Remittances)
        {
            var data = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["run_code"] = released.RunCode, ["provider_id"] = remittance.ProviderId, ["amount"] = remittance.Amount, ["currency"] = released.Currency,
                ["released_on"] = released.ReleasedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            }, JsonSerializerOptions.Web);
            var document = await generator.Generate(new GenerateDocument(template.TemplateCode, template.Language, template.DocumentType, HealthReferences.RemittanceRefType,
                remittance.RemittanceId, null, null, data), cancellationToken);
            if (document.IsFailure)
            {
                throw new InvalidOperationException($"The remittance advice for {remittance.ProviderId} in run {released.RunCode} was not generated: {document.Error.Message}");
            }
        }
    }
}
