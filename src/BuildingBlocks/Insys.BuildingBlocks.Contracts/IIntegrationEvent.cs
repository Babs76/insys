namespace Insys.BuildingBlocks.Contracts;

/// <summary>
/// An event published to other modules. It lives in the publishing module's Contracts project and carries
/// references, not sensitive values (INT-007).
/// </summary>
public interface IIntegrationEvent;

/// <summary>
/// Names an integration event. <see cref="Type"/> is "{context}.{EventName}", using the context ID and event name
/// from domain-model.json. The context prefix is needed because some event names occur in two contexts
/// (for example ReconciliationExceptionRaised).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IntegrationEventAttribute(string type, short version = 1) : Attribute
{
    public string Type { get; } = type;

    public short Version { get; } = version;
}
