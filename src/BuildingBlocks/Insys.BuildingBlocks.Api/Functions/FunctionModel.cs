/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Api
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
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Insys.BuildingBlocks.Api.Functions;

// ADR-026 and docs/specs/addenda/function-catalogue.md: a role is granted functions (screens, actions, jobs) from one
// global catalogue. The catalogue is generated from these declarations: each module maps its operations to functions,
// and static data functions follow the catalogue modules. Nothing here is hand-copied into the database.

public enum FunctionKind
{
    Screen,
    Action,
    Job,
}

/// <summary>Who a function is for (channel audiences; members are CUSTOMER).</summary>
public static class Audiences
{
    public const string Platform = "PLATFORM";
    public const string Staff = "STAFF";
    public const string Broker = "BROKER";
    public const string Provider = "PROVIDER";
    public const string Customer = "CUSTOMER";

    public static readonly IReadOnlyList<string> All = [Platform, Staff, Broker, Provider, Customer];

    /// <summary>Identity & Access, Static Data, Audit and Localisation serve both, without duplicated functions (sign-off decision 1).</summary>
    public static readonly IReadOnlyList<string> StaffAndPlatform = [Staff, Platform];
}

/// <summary>The features a tenant is entitled to (OQ-091); a function with none is base platform.</summary>
public static class Features
{
    public const string Life = "LIFE";
    public const string General = "GENERAL";
    public const string Health = "HEALTH";
    public const string Reinsurance = "REINSURANCE";
    public const string BrokerPortal = "BROKER_PORTAL";
    public const string ProviderPortal = "PROVIDER_PORTAL";
    public const string CustomerPortal = "CUSTOMER_PORTAL";

    public static readonly IReadOnlyList<string> All = [Life, General, Health, Reinsurance, BrokerPortal, ProviderPortal, CustomerPortal];
}

/// <summary>
/// One function. A SCREEN has a route; an ACTION has a SCREEN parent; a JOB is SYSTEM and has neither. Required
/// permissions are those of the operations it covers (x-permission and x-additional-permissions), plus any given.
/// </summary>
public sealed record FunctionDeclaration(string Code, FunctionKind Kind, string SubDomain, string ProcessArea, string? Parent, IReadOnlyList<string> Audiences,
    string? RouteKey, string? Feature, bool Privileged, IReadOnlyList<string> Endpoints, IReadOnlyList<string> Permissions)
{
    public string InteractionType => Kind == FunctionKind.Job ? "SYSTEM" : "HUMAN";
}

/// <summary>A module's functions, built fluently with the module's defaults (its audience and feature).</summary>
public sealed class FunctionMap(IReadOnlyList<string> audiences, string? feature)
{
    private readonly List<FunctionDeclaration> _functions = [];

    public IReadOnlyList<FunctionDeclaration> Functions => _functions;

    /// <summary>A screen in a sub-domain and process area, reached by the listed read operations (by endpoint name).</summary>
    public FunctionMap Screen(string code, string subDomain, string processArea, string routeKey, params string[] endpoints) =>
        Add(new FunctionDeclaration(code, FunctionKind.Screen, subDomain, processArea, null, audiences, routeKey, feature, false, endpoints, []));

    /// <summary>An action on a screen's records, by its operation (endpoint name).</summary>
    public FunctionMap Action(string code, string parent, string endpoint, bool privileged = false) =>
        Add(new FunctionDeclaration(code, FunctionKind.Action, Parent(parent).SubDomain, Parent(parent).ProcessArea, parent, audiences, null, feature, privileged,
            [endpoint], []));

    /// <summary>An action several operations share (approve and reject, say).</summary>
    public FunctionMap Action(string code, string parent, string[] endpoints, bool privileged = false) =>
        Add(new FunctionDeclaration(code, FunctionKind.Action, Parent(parent).SubDomain, Parent(parent).ProcessArea, parent, audiences, null, feature, privileged,
            endpoints, []));

    /// <summary>An action a handler checks with a permission of its own, beside an operation (x-additional-permissions).</summary>
    public FunctionMap Permission(string code, string parent, string permission) =>
        Add(new FunctionDeclaration(code, FunctionKind.Action, Parent(parent).SubDomain, Parent(parent).ProcessArea, parent, audiences, null, feature, false, [],
            [permission]));

    /// <summary>System work (a job or the module's event consumers); disabled for a tenant, its work parks (ADR-022).</summary>
    public FunctionMap Job(string code, string subDomain, string processArea) =>
        Add(new FunctionDeclaration(code, FunctionKind.Job, subDomain, processArea, null, [], null, feature, false, [], []));

    private FunctionDeclaration Parent(string code) =>
        _functions.SingleOrDefault(f => f.Code == code && f.Kind == FunctionKind.Screen)
        ?? throw new InvalidOperationException($"{code} is not a screen declared before its actions.");

    private FunctionMap Add(FunctionDeclaration function)
    {
        if (_functions.Any(f => f.Code == function.Code))
        {
            throw new InvalidOperationException($"Function {function.Code} is declared twice.");
        }

        _functions.Add(function);
        return this;
    }
}

/// <summary>A function as the catalogue records it; Order is the order of declaration, which orders the menu.</summary>
public sealed record CatalogueFunction(FunctionDeclaration Function, IReadOnlyList<string> Permissions, bool AllowedWhileOnboarding, int Order);

/// <summary>
/// The endpoint's function (x-function), checked with its permission (ADR-026). A portal operation shared by two portals
/// has one function per portal (each its own audience); the caller needs any one of them.
/// </summary>
public sealed record RequiredFunctionMetadata(params string[] Codes)
{
    public string Code => Codes[0];
}

/// <summary>
/// Every function and the permissions its operations require, gathered as the router builds the endpoints (a Finally
/// convention), so the catalogue generated from it is exactly what is enforced. Static across hosts: the code is the same.
/// </summary>
public static class FunctionRegistry
{
    private static readonly ConcurrentDictionary<string, FunctionDeclaration> Declared = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Observed = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string[]> EndpointFunctions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> RefusedWhileOnboarding = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> DeclarationOrder = new(StringComparer.Ordinal);

    public static void Declare(IEnumerable<FunctionDeclaration> functions)
    {
        foreach (var function in functions)
        {
            Declared[function.Code] = function;
            DeclarationOrder.TryAdd(function.Code, DeclarationOrder.Count + 1);
            foreach (var endpoint in function.Endpoints)
            {
                // Only portal functions share an operation, one per audience (sign-off decision 3).
                var codes = EndpointFunctions.GetValueOrDefault(endpoint, []);
                if (codes.Where(c => c != function.Code).Select(c => Declared[c]).Any(other => other.Audiences.Intersect(function.Audiences).Any()))
                {
                    throw new InvalidOperationException($"Endpoint {endpoint} is mapped to {function.Code} and to another function for the same audience.");
                }

                EndpointFunctions[endpoint] = [.. codes.Append(function.Code).Distinct(StringComparer.Ordinal)];
            }
        }
    }

    public static string[]? ForEndpoint(string endpointName) => EndpointFunctions.TryGetValue(endpointName, out var codes) ? codes : null;

    /// <summary>Records the permissions an endpoint of the function requires.</summary>
    public static void Observe(string functionCode, IEnumerable<string> permissions)
    {
        var set = Observed.GetOrAdd(functionCode, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        foreach (var permission in permissions)
        {
            set[permission] = 0;
        }
    }

    /// <summary>An operation of the function is a business transaction, refused while the tenant is Onboarding (today's rule).</summary>
    public static void ObserveRefusedWhileOnboarding(string functionCode) => RefusedWhileOnboarding[functionCode] = 0;

    /// <summary>
    /// Every declared function with its required permissions (its operations' and its own) and whether it is open while the
    /// tenant is Onboarding: none of its operations is refused then, and an action checked in a handler follows its screen.
    /// </summary>
    public static IReadOnlyList<CatalogueFunction> All() =>
        Declared.Values.OrderBy(f => f.Code, StringComparer.Ordinal)
            .Select(f => new CatalogueFunction(f, Required(f), OpenWhileOnboarding(f), DeclarationOrder[f.Code]))
            .ToList();

    // A screen with no reading operation of its own (a page that only starts its actions) requires what its actions require.
    private static IReadOnlyList<string> Required(FunctionDeclaration f)
    {
        var own = Own(f);
        return own.Count > 0 || f.Kind != FunctionKind.Screen
            ? own
            : Declared.Values.Where(a => a.Parent == f.Code).SelectMany(Own).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyList<string> Own(FunctionDeclaration f) =>
        f.Permissions.Concat(Observed.TryGetValue(f.Code, out var seen) ? seen.Keys : []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static bool OpenWhileOnboarding(FunctionDeclaration f) =>
        f is { Kind: FunctionKind.Action, Endpoints.Count: 0, Parent: { } parent } && f.Permissions.Count > 0
            ? !RefusedWhileOnboarding.ContainsKey(parent)
            : !RefusedWhileOnboarding.ContainsKey(f.Code);

    /// <summary>Endpoint names declared in a module's map that no endpoint has (a typo, or an operation removed).</summary>
    public static IReadOnlyList<string> Unmatched(IEnumerable<string> endpointNames)
    {
        var names = endpointNames.ToHashSet(StringComparer.Ordinal);
        return EndpointFunctions.Keys.Where(e => !names.Contains(e)).Order(StringComparer.Ordinal).ToList();
    }
}

public static class FunctionConventions
{
    /// <summary>
    /// Gives each endpoint of a module group the function its module maps it to, and records the endpoint's permissions
    /// for that function, as the router builds it.
    /// </summary>
    public static void Attach(RouteGroupBuilder group) =>
        ((IEndpointConventionBuilder)group).Finally(endpoint =>
        {
            if (endpoint.Metadata.OfType<RequiredFunctionMetadata>().Any())
            {
                Record(endpoint);
                return;
            }

            if (endpoint.Metadata.OfType<IEndpointNameMetadata>().LastOrDefault()?.EndpointName is { } name && FunctionRegistry.ForEndpoint(name) is { } codes)
            {
                endpoint.Metadata.Add(new RequiredFunctionMetadata(codes));
                Record(endpoint);
            }
        });

    private static void Record(EndpointBuilder endpoint)
    {
        // The operation's own permission only: a handler's extra check (x-additional-permissions) applies only in some cases, so
        // it is a function of its own (FunctionMap.Permission), not a requirement of this one.
        var permissions = endpoint.Metadata.OfType<RequiredPermissionMetadata>().Select(p => p.Permission)
            .Where(p => p is not (EndpointSecurityExtensions.SignedInUser or EndpointSecurityExtensions.SignedProviderCallback)).ToList();
        var refusedWhileOnboarding = endpoint.Metadata.OfType<BusinessTransactionMetadata>().Any()
            && !endpoint.Metadata.OfType<OnboardingConfigurationMetadata>().Any() && !endpoint.Metadata.OfType<StaticDataEndpointMetadata>().Any()
            && endpoint.Metadata.OfType<HttpMethodMetadata>().LastOrDefault()?.HttpMethods.Any(m => m is not ("GET" or "HEAD")) == true;
        foreach (var code in endpoint.Metadata.OfType<RequiredFunctionMetadata>().Last().Codes)
        {
            FunctionRegistry.Observe(code, permissions);
            if (refusedWhileOnboarding)
            {
                FunctionRegistry.ObserveRefusedWhileOnboarding(code);
            }
        }
    }
}

/// <summary>
/// The static data approve and reject operations are one route for every table (ADR-009): their function is the table's
/// catalogue module's approve function, found from the route's module and entity when the request comes.
/// </summary>
public sealed record StaticDataDecisionFunctionMetadata;

