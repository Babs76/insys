using System.Reflection;

namespace Insys.BuildingBlocks.Contracts;

public static class IntegrationEventTypes
{
    public static IntegrationEventAttribute Describe(Type eventType) =>
        eventType.GetCustomAttribute<IntegrationEventAttribute>()
        ?? throw new InvalidOperationException($"{eventType.Name} has no [IntegrationEvent] attribute.");
}
