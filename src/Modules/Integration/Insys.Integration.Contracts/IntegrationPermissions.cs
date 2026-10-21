namespace Insys.Integration.Contracts;

/// <summary>
/// Permission codes for the Integration module, in the iam.ref_permission_catalogue format "resource.operation".
/// The catalogue's operation enum is {read, create, update, approve, delete}, so replay and discard use "update" (OQ-007).
/// </summary>
public static class IntegrationPermissions
{
    public const string DeadLetterRead = "dead_letter.read";
    public const string DeadLetterUpdate = "dead_letter.update";
}
