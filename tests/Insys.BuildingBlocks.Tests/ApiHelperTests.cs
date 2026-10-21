using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;

namespace Insys.BuildingBlocks.Tests;

public sealed class ApiHelperTests
{
    [Fact]
    public void Cursor_RoundTrips_AndRejectsGarbage()
    {
        var id = Guid.CreateVersion7();
        Cursor.TryDecode(Cursor.Encode(id), out var decoded).ShouldBeTrue();
        decoded.ShouldBe(id);
        Cursor.TryDecode("not-a-cursor!", out _).ShouldBeFalse();
        Cursor.TryDecode(null, out var none).ShouldBeTrue();
        none.ShouldBeNull();
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData(0, 50)]
    [InlineData(10, 10)]
    [InlineData(5000, 200)]
    public void Cursor_ClampLimit(int? requested, int expected) => Cursor.ClampLimit(requested).ShouldBe(expected);

    [Fact]
    public void IfMatch_Missing_IsPreconditionRequired_DBA011()
    {
        var result = Concurrency.ExpectedVersion(new DefaultHttpContext().Request);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionRequired);
        Problems.StatusFor(result.Error.Kind).ShouldBe(428);
    }

    [Theory]
    [InlineData("\"7\"", 7L)]
    [InlineData(" \"42\" ", 42L)]
    public void IfMatch_StrongETag_GivesVersion_DBA011(string header, long expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfMatch = header;

        Concurrency.ExpectedVersion(context.Request).Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("W/\"7\"")]
    [InlineData("\"abc\"")]
    [InlineData("*")]
    public void IfMatch_WeakOrUnreadable_IsPreconditionFailed_DBA011(string header)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfMatch = header;

        Concurrency.ExpectedVersion(context.Request).Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
    }

    [Theory]
    [InlineData(ErrorKind.Validation, 422)]
    [InlineData(ErrorKind.Conflict, 409)]
    [InlineData(ErrorKind.NotFound, 404)]
    [InlineData(ErrorKind.Forbidden, 403)]
    [InlineData(ErrorKind.PreconditionFailed, 412)]
    public void Problems_MapErrorKinds_ToPromptStatusCodes(ErrorKind kind, int status) => Problems.StatusFor(kind).ShouldBe(status);

    [Theory]
    [InlineData("RowVersion", "row_version")]
    [InlineData("TenantId", "tenant_id")]
    [InlineData("FirstFailedAt", "first_failed_at")]
    [InlineData("IsoCode", "iso_code")]
    [InlineData("HTTPStatus", "http_status")]
    [InlineData("Last4", "last4")]
    public void SnakeCase_MatchesSchemaColumnNames(string name, string column) => SnakeCase.From(name).ShouldBe(column);
}
