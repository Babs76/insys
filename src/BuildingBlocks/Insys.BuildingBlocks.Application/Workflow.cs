using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Application;

/// <summary>
/// The owning domain's business validation for a workflow transition (WFL-002). A workflow definition names it in
/// domain_validation (for example "general.endorsement.validate"); the module that owns the business object registers
/// an implementation, and the Workflow module calls it before every transition. A definition that names a validator
/// nobody registered is refused, never skipped.
/// </summary>
public interface IWorkflowTransitionValidator
{
    /// <summary>The service code the definitions use.</summary>
    string ServiceCode { get; }

    Task<Result> Validate(string businessRefType, Guid businessRefId, string fromState, string toState, IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken);
}
