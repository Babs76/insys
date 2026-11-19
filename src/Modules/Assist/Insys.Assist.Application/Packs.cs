/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;

namespace Insys.Assist.Application;

/// <summary>One row of a pack, read from a pack file (docs/specs/starter-packs), with its marks and their sources (D-20).</summary>
public sealed record PackRow(int LineNo, string Table, string Code, string Class, bool Regulated, string RegulatedSource, bool Tailorable, string TailorableSource,
    string Values, string? RowStatus, string? Note, string? BasisToCheck, IReadOnlyList<string>? TailorColumns = null, IReadOnlyList<string>? TailorFrom = null);

/// <summary>A pack file, read: its header and rows. The file's own status is ignored: a version's status is INSYS's (§5.3).</summary>
public sealed record PackFile(string PackCode, string Kind, string? BuildsOn, string? Country, string? Line, IReadOnlyList<PackRow> Rows)
{
    private static readonly string[] Classes = ["OPERATIONAL", "FINANCE", "COMPLIANCE"];

    /// <summary>
    /// Reads a pack file. A row's `regulated` and `tailorable` marks are taken as given; a missing `tailorable` is false, and a missing `regulated` is read from the
    /// class (FINANCE or COMPLIANCE), each recorded with its source (D-20). Values are kept as given, in a canonical form for the hash.
    /// </summary>
    public static Result<PackFile> Read(JsonElement file)
    {
        if (file.ValueKind != JsonValueKind.Object || Text(file, "pack") is not { Length: > 0 } code || Text(file, "kind") is not { } kind || !PackKinds.All.Contains(kind)
            || !file.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A pack file has a pack code, a kind (BASE, COUNTRY, REGION or LINE) and at least one row.");
        }

        var read = new List<PackRow>();
        foreach (var row in rows.EnumerateArray())
        {
            var line = read.Count + 1;
            if (Text(row, "table") is not { Length: > 0 } table || Text(row, "code") is not { Length: > 0 } rowCode || Text(row, "class") is not { } @class
                || !Classes.Contains(@class, StringComparer.Ordinal) || !row.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
            {
                return Error.Validation(ErrorCodes.ValidationFailed, $"Row {line} needs a table, a code, a class (OPERATIONAL, FINANCE or COMPLIANCE) and its values.");
            }

            if (Mark(row, "regulated").Error is not null || Mark(row, "tailorable").Error is not null)
            {
                return Error.Validation(ErrorCodes.ValidationFailed, $"Row {line}: regulated and tailorable are true or false.");
            }

            var regulated = Mark(row, "regulated").Value;
            var tailorable = Mark(row, "tailorable").Value;
            var tailor = Tailor(row, values);
            if (tailor.IsFailure)
            {
                return Error.Validation(ErrorCodes.ValidationFailed, $"Row {line}: {tailor.Error.Message}");
            }

            if (tailor.Value is not null && tailorable != true)
            {
                return Error.Validation(ErrorCodes.ValidationFailed, $"Row {line}: tailor is given only on a row marked tailorable true.");
            }

            read.Add(new PackRow(line, table, rowCode, @class,
                regulated ?? @class is "FINANCE" or "COMPLIANCE", regulated is null ? MarkSources.Interim : MarkSources.Explicit,
                tailorable ?? false, tailorable is null ? MarkSources.Default : MarkSources.Explicit,
                PackHash.Canonical(values.GetRawText()), Text(row, "status"), Text(row, "note"), Text(row, "basis_to_check"), tailor.Value?.Columns, tailor.Value?.From));
        }

        return new PackFile(code, kind, Text(file, "builds_on"), kind is PackKinds.Country && code.Length == 2 ? code.ToUpperInvariant() : null,
            kind == PackKinds.Line ? code.Split('_')[0].ToUpperInvariant() : null, read);
    }

    /// <summary>The version's content hash (§5.3), as <see cref="PackHash"/> computes it.</summary>
    public string Hash(int version, string scope) => PackHash.Of(PackCode, Kind, version, scope, BuildsOn, Country, Line,
        Rows.Select(r => new PackHash.Row(r.LineNo, r.Table, r.Code, r.Class, r.Regulated, r.RegulatedSource, r.Tailorable, r.TailorableSource, r.Values, r.RowStatus, r.Note, r.BasisToCheck,
            r.TailorColumns, r.TailorFrom)));

    /// <summary>
    /// What tailoring may change, and from what (OQ-187 (a), Ade, 19 November 2026): `tailor` names the row's columns and the questionnaire answers that may
    /// set them, as `{ "columns": [...], "from": [...] }`. Each column is one of the row's values; each answer is a questionnaire field. Absent, nothing is tailored.
    /// </summary>
    private static Result<(IReadOnlyList<string> Columns, IReadOnlyList<string> From)?> Tailor(JsonElement row, JsonElement values)
    {
        if (!row.TryGetProperty("tailor", out var tailor) || tailor.ValueKind == JsonValueKind.Null)
        {
            return ((IReadOnlyList<string>, IReadOnlyList<string>)?)null;
        }

        static IReadOnlyList<string>? Names(JsonElement e, string name) =>
            e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
            && list.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                ? [.. list.EnumerateArray().Select(v => v.GetString()!).Distinct(StringComparer.Ordinal)]
                : null;

        if (tailor.ValueKind != JsonValueKind.Object || Names(tailor, "columns") is not { } columns || Names(tailor, "from") is not { } from)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "tailor names its columns and the answers they come from: { \"columns\": [...], \"from\": [...] }.");
        }

        if (columns.FirstOrDefault(c => !values.TryGetProperty(c, out _)) is { } unknownColumn)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"tailor names {unknownColumn}, which isn't one of the row's values.");
        }

        if (from.FirstOrDefault(f => !SetupAnswerFields.All.Contains(f, StringComparer.Ordinal)) is { } unknownAnswer)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"tailor names {unknownAnswer}, which isn't a questionnaire answer ({string.Join(", ", SetupAnswerFields.All)}).");
        }

        return ((IReadOnlyList<string>, IReadOnlyList<string>)?)(columns, from);
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static (bool? Value, string? Error) Mark(JsonElement row, string name) =>
        !row.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null ? (null, null)
        : v.ValueKind is JsonValueKind.True or JsonValueKind.False ? (v.GetBoolean(), null) : (null, name);
}

/// <summary>
/// A version's content hash (§5.3): its header and every row in order, everything the review sheet shows of it, its marks and their sources, values in a
/// canonical order. Computed when the file is loaded, and again from the stored rows at approval, so an approval binds to exactly what was reviewed.
/// </summary>
public static class PackHash
{
    public sealed record Row(int LineNo, string Table, string Code, string Class, bool Regulated, string RegulatedSource, bool Tailorable, string TailorableSource, string Values,
        string? RowStatus, string? Note, string? BasisToCheck, IReadOnlyList<string>? TailorColumns = null, IReadOnlyList<string>? TailorFrom = null);

    public static string Of(string packCode, string kind, int version, string scope, string? buildsOn, string? country, string? line, IEnumerable<Row> rows)
    {
        static string F(string? v) => v is null ? "\u0000" : v.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{F(packCode)}|{kind}|{version}|{scope}|{F(buildsOn)}|{F(country)}|{F(line)}\n"));
        foreach (var r in rows.OrderBy(r => r.LineNo))
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{r.LineNo}|{F(r.Table)}|{F(r.Code)}|{r.Class}|{r.Regulated}|{r.RegulatedSource}|{r.Tailorable}|{r.TailorableSource}|{F(Canonical(r.Values))}|{F(r.RowStatus)}|{F(r.Note)}|{F(r.BasisToCheck)}");
            // OQ-187: what tailoring may change is part of what is reviewed. Appended only when given, so a row without it hashes as before.
            if (r.TailorColumns is { Count: > 0 } || r.TailorFrom is { Count: > 0 })
            {
                text.Append(CultureInfo.InvariantCulture, $"|tailor:{F(string.Join(",", r.TailorColumns ?? []))}:{F(string.Join(",", r.TailorFrom ?? []))}");
            }

            text.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    /// <summary>JSON with its object keys sorted and no spacing, so the same values hash the same as read from a file or from the database's jsonb.</summary>
    public static string Canonical(string json) => Sorted(JsonNode.Parse(json))?.ToJsonString() ?? "null";

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(Sorted)]),
        null => null,
        JsonValue v when v.TryGetValue<decimal>(out var number) && v.GetValueKind() == JsonValueKind.Number => JsonValue.Create(number),
        _ => JsonNode.Parse(node.ToJsonString()),
    };

    public static string Of(StarterPack pack, IReadOnlyList<StarterPackValue> rows) => Of(pack.PackCode, pack.Kind, pack.Version, pack.Scope, pack.BuildsOn, pack.Country, pack.Line,
        rows.Select(r => new Row(r.LineNo, r.TableName, r.RowCode, r.Class, r.Regulated, r.RegulatedSource, r.Tailorable, r.TailorableSource, r.Values, r.RowStatus, r.Note, r.BasisToCheck,
            r.TailorColumns, r.TailorFrom)));
}

/// <summary>A version as listed.</summary>
public sealed record StarterPackView(Guid Id, string PackCode, string Kind, int Version, string? BuildsOn, string? Country, string? Line, string Scope, string Status,
    string ContentHash, int RowCount, string PreparedBy, string? ReviewedBy, string? ReviewPartner, string? ReviewerName, DateOnly? ReviewedOn, Guid? ReviewDocumentId,
    string? ApprovedBy, DateTimeOffset? ApprovedAt, long RowVersion)
{
    public static StarterPackView From(StarterPack p) => new(p.Id, p.PackCode, p.Kind, p.Version, p.BuildsOn, p.Country, p.Line, p.Scope, p.Status, p.ContentHash, p.RowCount,
        p.PreparedBy, p.ReviewedBy, p.ReviewPartner, p.ReviewerName, p.ReviewedOn, p.ReviewDocumentId, p.ApprovedBy, p.ApprovedAt, p.RowVersion);
}

/// <summary>
/// The review sheet's row (§5.3, D-20): what the reviewer and the approver confirm. Every row is listed, its class, both marks and where each came from, and
/// whether AI may tailor it in 7.8 (an explicit mark only).
/// </summary>
public sealed record PackSheetRow(int LineNo, string Table, string Code, string Class, bool Regulated, string RegulatedSource, bool Tailorable, string TailorableSource,
    bool MayBeTailored, JsonElement Values, string? RowStatus, string? Note, string? BasisToCheck, IReadOnlyList<string>? TailorColumns = null, IReadOnlyList<string>? TailorFrom = null);

public sealed record StarterPackSheet(StarterPackView Pack, IReadOnlyList<PackSheetRow> Rows, int InterimMarks);

public interface IStarterPacks
{
    /// <summary>A version the tenant may read: a platform version, or its own.</summary>
    Task<StarterPack?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<StarterPackValue>> Rows(Guid packId, CancellationToken cancellationToken);

    /// <summary>The versions the tenant may read, newest first.</summary>
    Task<IReadOnlyList<StarterPack>> List(string? packCode, CancellationToken cancellationToken);

    /// <summary>The highest version number of a pack held by this tenant.</summary>
    Task<int> LastVersion(string packCode, CancellationToken cancellationToken);

    /// <summary>Whether this person loaded or reviewed any version of the pack held by this tenant (§5.3: reloading doesn't make a new person of them).</summary>
    Task<bool> PreparedOrReviewedAny(string packCode, string actor, CancellationToken cancellationToken);

    /// <summary>Whether this person ran a setup that used the version (§5.3: whoever ran the wizard can't approve).</summary>
    Task<bool> UsedInSetupBy(Guid packId, string actor, CancellationToken cancellationToken);

    void Add(StarterPack pack, IReadOnlyList<StarterPackValue> rows);
}

/// <param name="Scope">ALL_TENANTS, from the platform's route, or TENANT_ONLY, from a tenant's (the fallback, D-19).</param>
public sealed record LoadStarterPack(string Scope, JsonElement File) : ICommand<StarterPackView>;

public sealed record MarkStarterPackReviewed(Guid Id, string Partner, string ReviewerName, DateOnly ReviewedOn, Guid ReviewDocumentId, long? ExpectedVersion) : ICommand<StarterPackView>;

/// <param name="Route">The scope the approver's function approves: ALL_TENANTS (ASSIST_OVERLAY_APPROVE) or TENANT_ONLY (ASSIST_OVERLAY_APPROVE_TENANT).</param>
public sealed record ApproveStarterPack(Guid Id, string ContentHash, string Route, long? ExpectedVersion) : ICommand<StarterPackView>;

public sealed record GetStarterPack(Guid Id) : IQuery<StarterPackSheet>;

/// <param name="AwaitingApproval">Only the versions this tenant holds that its approver may approve now: REVIEWED for every tenant, or its own DRAFT or REVIEWED.</param>
public sealed record ListStarterPacks(string? PackCode, bool AwaitingApproval = false) : IQuery<IReadOnlyList<StarterPackView>>;

internal sealed class StarterPackHandlers(IStarterPacks packs, IExecutionContext execution, ITenantContext tenant, IClock clock, IAuditTrail audit,
    Tenancy.Contracts.ITenantStatusQuery tenants, Microsoft.Extensions.Options.IOptions<PlatformOptions> platform)
    : ICommandHandler<LoadStarterPack, StarterPackView>, ICommandHandler<MarkStarterPackReviewed, StarterPackView>, ICommandHandler<ApproveStarterPack, StarterPackView>,
        IQueryHandler<GetStarterPack, StarterPackSheet>, IQueryHandler<ListStarterPacks, IReadOnlyList<StarterPackView>>
{
    public async Task<Result<StarterPackView>> Handle(LoadStarterPack command, CancellationToken cancellationToken)
    {
        // A tenant's own version, the fallback (D-19), is loaded only while it is onboarding (Ade, 15 November).
        if (command.Scope == PackScopes.TenantOnly
            && await tenants.UserAccess(tenant.RequireTenantId(), cancellationToken) != Tenancy.Contracts.TenantUserAccess.ConfigurationOnly)
        {
            return Error.Conflict(PackErrorCodes.TenantLive, "A tenant's own overlay is loaded only while the tenant is onboarding.");
        }

        // A version for every tenant is held by the operator tenant alone (D-18): the platform's routes say so, and this says it again.
        if (command.Scope == PackScopes.AllTenants && (platform.Value.OperatorTenantId == Guid.Empty || tenant.TenantId != platform.Value.OperatorTenantId))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "A version for every tenant is loaded by the platform operator only.");
        }

        var file = PackFile.Read(command.File);
        if (file.IsFailure)
        {
            return file.Error;
        }

        var version = await packs.LastVersion(file.Value.PackCode, cancellationToken) + 1;
        var hash = file.Value.Hash(version, command.Scope);
        var loaded = StarterPack.Load(file.Value.PackCode, file.Value.Kind, version, file.Value.BuildsOn, file.Value.Country, file.Value.Line, command.Scope, hash,
            file.Value.Rows.Count, execution.ActorId);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var pack = loaded.Value;
        packs.Add(pack, [.. file.Value.Rows.Select(r => StarterPackValue.For(pack.Id, pack.Scope, r.LineNo, r.Table, r.Code, r.Class, r.Regulated, r.RegulatedSource, r.Tailorable,
            r.TailorableSource, r.Values, r.RowStatus, r.Note, r.BasisToCheck, r.TailorColumns, r.TailorFrom))]);
        audit.Record(new AuditEntry("ASSIST", "LoadStarterPack", nameof(StarterPack), pack.Id.ToString(), new Dictionary<string, string?>
        {
            ["pack"] = pack.PackCode, ["version"] = version.ToString(CultureInfo.InvariantCulture), ["scope"] = pack.Scope, ["hash"] = hash,
        }));
        return StarterPackView.From(pack);
    }

    public async Task<Result<StarterPackView>> Handle(MarkStarterPackReviewed command, CancellationToken cancellationToken)
    {
        var found = await Own(command.Id, command.ExpectedVersion, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var reviewed = found.Value.MarkReviewed(execution.ActorId, command.Partner, command.ReviewerName, command.ReviewedOn, command.ReviewDocumentId);
        if (reviewed.IsFailure)
        {
            return reviewed.Error;
        }

        audit.Record(new AuditEntry("ASSIST", "MarkStarterPackReviewed", nameof(StarterPack), found.Value.Id.ToString(), new Dictionary<string, string?>
        {
            ["partner"] = found.Value.ReviewPartner, ["reviewedOn"] = found.Value.ReviewedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["document"] = found.Value.ReviewDocumentId?.ToString(),
        }));
        return StarterPackView.From(found.Value);
    }

    public async Task<Result<StarterPackView>> Handle(ApproveStarterPack command, CancellationToken cancellationToken)
    {
        var found = await Own(command.Id, command.ExpectedVersion, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var pack = found.Value;
        // §5.3: only the pack's newest version, so a correction isn't overtaken by the version it corrects.
        if (await packs.LastVersion(pack.PackCode, cancellationToken) != pack.Version)
        {
            return Error.Conflict(PackErrorCodes.NotNewest, "Only a pack's newest version can be approved.");
        }

        // Whoever loaded or reviewed any version of the pack, or ran a setup that used this one, can't approve it (§5.3), whatever functions they hold.
        if (await packs.PreparedOrReviewedAny(pack.PackCode, execution.ActorId, cancellationToken) || await packs.UsedInSetupBy(pack.Id, execution.ActorId, cancellationToken))
        {
            return Error.Forbidden(ErrorCodes.MakerCheckerSamePerson, "Whoever loaded or reviewed a version of this pack, or ran a setup with it, can't approve it.");
        }

        // The hash again, from the rows as stored: the approval binds to what is there now, all of it (§5.3).
        var rows = await packs.Rows(pack.Id, cancellationToken);
        if (rows.Count != pack.RowCount || PackHash.Of(pack, rows) != pack.ContentHash)
        {
            return Error.Conflict(PackErrorCodes.Changed, "The version's rows aren't the content that was loaded.");
        }

        var approved = pack.Approve(execution.ActorId, command.ContentHash, command.Route, clock.UtcNow);
        if (approved.IsFailure)
        {
            return approved.Error;
        }

        audit.Record(new AuditEntry("ASSIST", "ApproveStarterPack", nameof(StarterPack), found.Value.Id.ToString(), new Dictionary<string, string?>
        {
            ["pack"] = found.Value.PackCode, ["version"] = found.Value.Version.ToString(CultureInfo.InvariantCulture), ["scope"] = found.Value.Scope, ["hash"] = found.Value.ContentHash,
        }));
        return StarterPackView.From(found.Value);
    }

    public async Task<Result<StarterPackSheet>> Handle(GetStarterPack query, CancellationToken cancellationToken)
    {
        if (await packs.Get(query.Id, cancellationToken) is not { } pack)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The pack version was not found.");
        }

        var rows = (await packs.Rows(pack.Id, cancellationToken)).OrderBy(r => r.LineNo).Select(r => new PackSheetRow(r.LineNo, r.TableName, r.RowCode, r.Class, r.Regulated,
            r.RegulatedSource, r.Tailorable, r.TailorableSource, r.MayBeTailored, JsonDocument.Parse(r.Values).RootElement.Clone(), r.RowStatus, r.Note, r.BasisToCheck,
            r.TailorColumns, r.TailorFrom)).ToList();
        return new StarterPackSheet(StarterPackView.From(pack), rows, rows.Count(r => r.RegulatedSource == MarkSources.Interim));
    }

    public async Task<Result<IReadOnlyList<StarterPackView>>> Handle(ListStarterPacks query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<StarterPackView>>([.. (await packs.List(query.PackCode, cancellationToken))
            .Where(p => !query.AwaitingApproval || (p.TenantId == tenant.TenantId && p.Status != PackStatuses.Approved
                && (p.Scope == PackScopes.TenantOnly || p.Status == PackStatuses.Reviewed)))
            .Select(StarterPackView.From)]);

    /// <summary>A version this tenant holds, at the version the caller read (If-Match): only the holder reviews or approves it.</summary>
    private async Task<Result<StarterPack>> Own(Guid id, long? expected, CancellationToken cancellationToken)
    {
        if (await packs.Get(id, cancellationToken) is not { } pack || pack.TenantId != tenant.TenantId)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The pack version was not found.");
        }

        if (expected is null)
        {
            return new Error(ErrorCodes.PreconditionRequired, "This change needs an If-Match header with the ETag from a GET.", ErrorKind.PreconditionRequired);
        }

        return expected != pack.RowVersion ? Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The version has changed since it was read. Read it again.") : pack;
    }
}
