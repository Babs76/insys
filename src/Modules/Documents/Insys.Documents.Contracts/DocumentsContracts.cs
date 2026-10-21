using Insys.BuildingBlocks.Contracts;

namespace Insys.Documents.Contracts;

public static class DocumentPermissions
{
    public const string DocumentRead = "document.read";
    public const string DocumentCreate = "document.create";
    public const string DocumentDeliver = "document.deliver";
    public const string DocumentDispose = "document.dispose";
    public const string LegalHoldManage = "legal_hold.manage";
}

[IntegrationEvent("document.DocumentGenerated")]
public sealed record DocumentGenerated(Guid DocumentId, string CategoryCode, string BusinessRefType, Guid BusinessRefId, string? TemplateCode, int? TemplateVersion)
    : IIntegrationEvent;

[IntegrationEvent("document.DocumentDelivered")]
public sealed record DocumentDelivered(Guid DocumentId, string Channel) : IIntegrationEvent;

[IntegrationEvent("document.DocumentDisposed")]
public sealed record DocumentDisposed(Guid DocumentId) : IIntegrationEvent;
