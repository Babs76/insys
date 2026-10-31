/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ArchitectureTests
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
using System.Text.RegularExpressions;
using Insys.Testing;

namespace Insys.ArchitectureTests;

/// <summary>
/// An event mapper is a declaration that fails silently: a mapper not registered, or an arm not written, loses the event with no error and a
/// consumer waits for ever. Each rule reads the source, so it needs no container.
/// </summary>
public sealed partial class EventMapperRuleTests
{
    private static readonly Lazy<string> Source = new(() => string.Join('\n',
        Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)));

    [Fact]
    public void EveryEventMapper_IsRegistered()
    {
        var mappers = MapperClass().Matches(Source.Value).Select(m => m.Groups[1].Value).ToHashSet();
        mappers.Count.ShouldBeGreaterThan(10);
        Unregistered(mappers, Source.Value).ShouldBeEmpty("register it with AddDomainEventMapper");
    }

    [Fact]
    public void EveryEventAConsumerWaitsFor_IsBuiltSomewhere()
    {
        var subscribed = Subscription().Matches(Source.Value).Select(m => m.Groups[1].Value).Where(name => name != "TEvent").ToHashSet();
        subscribed.Count.ShouldBeGreaterThan(50);
        subscribed.Where(name => !Built(name, Source.Value)).ShouldBeEmpty("a consumer waits for an event nothing publishes");
    }

    [Fact]
    public void EveryDomainEvent_NamedLikeAPublishedEvent_HasAMapperArm()
    {
        var unmapped = Unmapped(Source.Value).ToList();
        unmapped.ShouldBeEmpty("add its arm to the module's event mapper, or rename it if it isn't published");
    }

    [Theory]
    [InlineData("record Paid(Guid Id) : IDomainEvent; record Paid(Guid Id) : IIntegrationEvent;", true)]
    [InlineData("record Paid(Guid Id) : IDomainEvent; record Paid(Guid Id) : IIntegrationEvent; class M { Domain.Paid e => new Contracts.Paid(e.Id) }", false)]
    [InlineData("record Paid(Guid Id) : IDomainEvent; record Paid(Guid Id) : IIntegrationEvent; class M { Domain.Paid paid when paid.Id != default => null }", false)]
    [InlineData("record Paid(Guid Id) : IDomainEvent; record Other(Guid Id) : IIntegrationEvent;", false)]
    public void TheRule_SeesAMissingArm(string code, bool flagged) => Unmapped(code).Any().ShouldBe(flagged);

    [Fact]
    public void TheRule_SeesAMapperNotRegistered() =>
        Unregistered(["PaidMapper", "OtherMapper"], "services.AddDomainEventMapper<OtherMapper>(schema)").ShouldBe(["PaidMapper"]);

    [Theory]
    [InlineData("new Contracts.Paid(id)", true)]
    [InlineData("new Paid (id)", true)]
    [InlineData("IIntegrationEventHandler<Paid>", false)]
    public void TheRule_SeesWhetherAnEventIsBuilt(string code, bool built) => Built("Paid", code).ShouldBe(built);

    private static IEnumerable<string> Unregistered(IEnumerable<string> mappers, string text) =>
        mappers.Where(mapper => !text.Contains($"AddDomainEventMapper<{mapper}>", StringComparison.Ordinal));

    private static bool Built(string name, string text) =>
        Regex.IsMatch(text, $@"new\s+(?:[\w.]+\.)?{name}\s*\(", RegexOptions.None, TimeSpan.FromSeconds(30));

    private static IEnumerable<string> Unmapped(string text)
    {
        var published = PublishedEvent().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
        var arms = MapperArm().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
        return DomainEvent().Matches(text).Select(m => m.Groups[1].Value).Distinct().Where(name => published.Contains(name) && !arms.Contains(name));
    }

    [GeneratedRegex(@"class (\w+)\s*(?:\([^)]*\))?\s*:\s*IDomainEventMapper")]
    private static partial Regex MapperClass();

    [GeneratedRegex(@"AddIntegrationEventHandler<\s*(?:[\w.]+\.)?(\w+)\s*,")]
    private static partial Regex Subscription();

    [GeneratedRegex(@"record (\w+)\s*\([^;]*?\)\s*:\s*IDomainEvent\b")]
    private static partial Regex DomainEvent();

    [GeneratedRegex(@"record (\w+)\s*\([^;]*?\)\s*:\s*IIntegrationEvent\b")]
    private static partial Regex PublishedEvent();

    /// <summary>A switch arm: the event's type, a variable, then a condition or the arrow.</summary>
    [GeneratedRegex(@"(?:[\w]+\.)*(\w+)\s+\w+\s+(?:when\b|=>)")]
    private static partial Regex MapperArm();
}
