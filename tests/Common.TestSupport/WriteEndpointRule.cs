using System.Reflection;
using Common.Application;
using Common.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Common.TestSupport;

/// <summary>§8.5's rule over a host's endpoint table: a write is keyed, or declares why a repeat is harmless.</summary>
/// <remarks>ADR-058 owns the rule and the hosts outside it; a host's suite pins what this selects.</remarks>
public static class WriteEndpointRule
{
    private static readonly string[] WriteVerbs =
    [
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete
    ];

    /// <summary>The endpoints a write verb reaches.</summary>
    public static IReadOnlyList<Endpoint> Writes(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Where(AcceptsAWrite)];

    /// <summary>The endpoints naming neither a method nor a handler, which <see cref="Writes"/> leaves out.</summary>
    /// <remarks>The framework's own, so a host's suite names each and a route of its own shows (ADR-058).</remarks>
    public static IReadOnlyList<Endpoint> Unrestricted(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Where(endpoint => NamesNoMethod(endpoint) && !HasHandler(endpoint))];

    /// <summary>Every endpoint that breaks the rule, each as a sentence naming it.</summary>
    public static IReadOnlyList<string> Offenders(IEnumerable<Endpoint> endpoints)
    {
        List<string> offenders = [];

        foreach (Endpoint endpoint in endpoints)
        {
            string name = NameOf(endpoint);
            string[] commands = [.. CommandsOf(endpoint).Select(command => command.Name)];
            string keyedBy = string.Join(" and ", commands);
            RetrySafety[] kinds = [.. KindsOf(endpoint)];

            if (commands.Length > 0)
                offenders.AddRange(Unauthenticated(endpoint, name, keyedBy));

            if (!AcceptsAWrite(endpoint))
                continue;

            if (commands.Length > 1)
            {
                offenders.Add(
                    $"{name} is keyed by {keyedBy}: a keyed endpoint dispatches one command, so it binds that " +
                    "command or declares it, and a second names a command it does not send (§8.5)");
            }

            if (commands.Length == 0 && kinds.Length == 0)
            {
                offenders.Add(
                    $"{name} accepts a write and is neither keyed nor declared retry-safe: bind an " +
                    "IIdempotentCommand, or say .Idempotent<TCommand>() or .RetrySafe(kind) (§8.5)");
            }

            if (commands.Length > 0 && kinds.Length > 0)
            {
                offenders.Add(
                    $"{name} is keyed by {keyedBy} and declared {kinds[0]}: a write endpoint is one or " +
                    "the other, and a declaration on its group reaches it (§8.5)");
            }

            if (kinds.Length > 1)
                offenders.Add($"{name} declares {string.Join(" and ", kinds)}: a repeat is harmless for one reason");
        }

        return offenders;
    }

    /// <summary>The same, and every idempotent command of <paramref name="application"/> no endpoint reaches.</summary>
    /// <remarks>Both directions, so a scan of the wrong assembly fails rather than finds nothing (§8.5).</remarks>
    public static IReadOnlyList<string> Offenders(IEnumerable<Endpoint> endpoints, Assembly application)
    {
        Endpoint[] table = [.. endpoints];
        List<string> offenders = [.. Offenders(table)];

        Type[] declared = [.. CommandFingerprintRule.IdempotentCommands(application)];
        Type[] reached = [.. table.SelectMany(CommandsOf).Distinct()];

        offenders.AddRange(
            declared
                .Except(reached)
                .Select(command =>
                    $"{command.Name} is an idempotent command no endpoint reaches: one reached only by a " +
                    "consumer is the inbox's to deduplicate and declares no IIdempotentCommand (§8.5, §9.5)"));

        offenders.AddRange(
            reached
                .Except(declared)
                .Select(command =>
                    $"{command.Name} keys an endpoint and is not an idempotent command of " +
                    $"{application.GetName().Name}, so this scan is reading the wrong assembly"));

        return offenders;
    }

    // An endpoint naming no method takes every verb. A bare RequestDelegate naming none is how the framework
    // maps §13.5's probes and gRPC's fallbacks, so only a route handler is read that way (ADR-058).
    private static bool AcceptsAWrite(Endpoint endpoint)
    {
        if (NamesNoMethod(endpoint))
            return HasHandler(endpoint);

        return endpoint.Metadata
            .GetMetadata<IHttpMethodMetadata>()!
            .HttpMethods
            .Any(method => WriteVerbs.Contains(method, StringComparer.OrdinalIgnoreCase));
    }

    private static bool NamesNoMethod(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is null or [];

    private static bool HasHandler(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<MethodInfo>() is not null;

    // Keyed by a handler parameter, or by the declaration an endpoint makes when it builds the command itself.
    private static Type[] CommandsOf(Endpoint endpoint) =>
    [
        .. (endpoint.Metadata.GetMetadata<MethodInfo>()?.GetParameters() ?? [])
            .Select(parameter => parameter.ParameterType)
            .Where(typeof(IIdempotentCommand).IsAssignableFrom)
            .Concat(endpoint.Metadata.GetOrderedMetadata<IdempotentCommandMetadata>().Select(keyed => keyed.Command))
            .Distinct()
    ];

    private static IEnumerable<RetrySafety> KindsOf(Endpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<RetrySafetyMetadata>().Select(declared => declared.Kind).Distinct();

    // §8.5's subject rule: an anonymous caller claims under the shared system subject.
    private static IEnumerable<string> Unauthenticated(Endpoint endpoint, string name, string keyedBy)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            yield return
                $"{name} is keyed by {keyedBy} and allows anonymous callers, who all claim under the " +
                "shared system subject (§8.5)";
        }

        if (endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0)
        {
            yield return
                $"{name} is keyed by {keyedBy} and requires no authorisation, so the caller has no " +
                "subject to key on (§8.5)";
        }
    }

    private static string NameOf(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ??
        endpoint.DisplayName ??
        "an unnamed endpoint";
}
