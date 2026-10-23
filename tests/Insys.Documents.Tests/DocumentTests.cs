/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Documents.Tests
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
using Insys.BuildingBlocks.Domain;
using Insys.Documents.Domain.Documents;
using Insys.Testing;

namespace Insys.Documents.Tests;

public sealed class DocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);

    private static Document Generated(DateOnly? retentionUntil) =>
        Document.Create(Guid.CreateVersion7(), "POLICY_SCHEDULE", "PERSONAL", new StoredContent("tenant/x/documents/y", "text/plain", 10, new string('0', 64)),
            new TemplateVersionRef("TPL-MOT-SCH", 7), "GEN-MOTOR-PV v4", "POLICY", Guid.CreateVersion7(), retentionUntil, Now);

    [Fact]
    public void Document_Lifecycle_CoversTheDomainModel()
    {
        // Requested → Generated is the generation request; the rest is the document's own lifecycle.
        var specified = DomainModelSpec.Transitions("document", "Document").Where(t => !t.StartsWith("Requested-", StringComparison.Ordinal))
            .Select(t => t.Replace("Retention ended, no hold", "RetentionEndedNoHold", StringComparison.Ordinal)).ToList();
        var implemented = Document.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").ToList();
        implemented.ShouldBeSubsetOf(specified.Append("Delivered-DeliverDocument->Delivered"));
        specified.ShouldBeSubsetOf(implemented);
    }

    [Fact]
    public void GeneratedDocument_RecordsItsTemplateAndProductVersions_DOC002()
    {
        var document = Generated(Today.AddYears(5));
        document.TemplateCode.ShouldBe("TPL-MOT-SCH");
        document.TemplateVersion.ShouldBe(7);
        document.ProductVersionRef.ShouldBe("GEN-MOTOR-PV v4");
        document.DomainEvents.OfType<DocumentGenerated>().Single().TemplateVersion.ShouldBe(7);
    }

    [Fact]
    public void Document_IsNotDisposedOf_BeforeItsRetentionDate() =>
        Generated(Today).DisposeDocument(Today, Now).Error!.Code.ShouldBe(DocumentErrorCodes.RetentionNotEnded);

    [Fact]
    public void Document_WithoutARetentionDate_IsKept() =>
        Generated(null).DisposeDocument(Today, Now).Error!.Code.ShouldBe(DocumentErrorCodes.RetentionNotEnded);

    [Fact]
    public void Document_OnLegalHold_IsNotDisposedOf_UntilReleased()
    {
        var document = Generated(Today.AddDays(-1));
        var hold = document.PlaceLegalHold("LITIGATION", Guid.CreateVersion7(), Now).Value;
        document.DisposeDocument(Today, Now).Error!.Code.ShouldBe(DocumentErrorCodes.OnLegalHold);

        document.ReleaseLegalHold(hold, Now).IsSuccess.ShouldBeTrue();
        document.LegalHold.ShouldBeFalse();
        document.DisposeDocument(Today, Now).IsSuccess.ShouldBeTrue();
        document.Status.ShouldBe(DocumentStatus.Disposed);
        document.DisposedAt.ShouldBe(Now);
    }

    [Fact]
    public void FailedDelivery_IsRecorded_ButDoesNotDeliver()
    {
        var document = Generated(null);
        document.DeliverDocument("EMAIL", "a@example.com", sent: false, null, Now).Value.Status.ShouldBe("FAILED");
        document.Status.ShouldBe(DocumentStatus.Generated);
        document.DeliverDocument("EMAIL", "a@example.com", sent: true, "P-1", Now).Value.Status.ShouldBe("SENT");
        document.Status.ShouldBe(DocumentStatus.Delivered);
        document.Deliveries.Count.ShouldBe(2);
    }

    [Fact]
    public void DisposedDocument_IsNotDelivered()
    {
        var document = Generated(Today.AddDays(-1));
        document.DisposeDocument(Today, Now).IsSuccess.ShouldBeTrue();
        document.DeliverDocument("EMAIL", "a@example.com", true, "P", Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Theory]
    [InlineData("PUBLIC", false, false)]
    [InlineData("INTERNAL", false, false)]
    [InlineData("PERSONAL", true, false)]
    [InlineData("HIGHLY_SENSITIVE", true, true)]
    public void Classification_DecidesAuditAndRoleChecks_DOC003_DOC004(string classification, bool audited, bool needsRole)
    {
        Classifications.IsSensitive(classification).ShouldBe(audited);
        Classifications.NeedsAllowedRole(classification).ShouldBe(needsRole);
    }
}
