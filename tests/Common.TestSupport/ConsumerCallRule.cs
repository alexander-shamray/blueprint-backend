using System.Reflection;
using System.Text.RegularExpressions;
using Common.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Common.TestSupport;

/// <summary>What a declared call gives up when its dependency is unreachable, in §8.1's terms.</summary>
public enum UnreachableChoice
{
    /// <summary>The work proceeds on what this side already holds.</summary>
    Availability,

    /// <summary>The work waits, since proceeding without the answer would be wrong.</summary>
    Correctness
}

/// <summary>ADR-017's written exception for one outbound client a consumer reaches.</summary>
/// <remarks>
/// <see cref="GrantedBy"/> names the ADR that grants it (§9.7); the two behaviours split unreachable from "answered
/// no" as ADR-052's table does.
/// </remarks>
public sealed record ConsumerCallException(
    Type Client,
    string GrantedBy,
    string WhenUnreachable,
    string WhenAnsweredNo,
    UnreachableChoice Chooses);

/// <summary>ADR-017's rule over a host's composition: a consumer reaches no undeclared outbound client.</summary>
/// <remarks>
/// Consumers alone: a worker loop is not one, and calling out from a row a worker leases is ADR-052's chosen shape. A
/// service locator in the graph is taken to reach every handler §6.2 resolves by type.
/// </remarks>
public static partial class ConsumerCallRule
{
    private static readonly string[] ClientFactoryAssemblies =
    [
        "Microsoft.Extensions.Http",
        "Grpc.Net.ClientFactory"
    ];

    private static readonly string[] ClientParameterNames =
    [
        "System.Net.Http.IHttpClientFactory",
        "Grpc.Net.ClientFactory.GrpcClientFactory"
    ];

    // Clients a type constructs for itself, which no registration or constructor shows.
    private static readonly string[] ConstructedClientNames =
    [
        "MailKit.MailService",
        "System.Net.Mail.SmtpClient"
    ];

    /// <summary>The consumers and saga state machines the host's composition registers.</summary>
    public static IReadOnlyList<Type> Consumers(IEnumerable<ServiceDescriptor> composition, Assembly host)
    {
        Func<Type, bool> platform = Platform(host);

        return
        [
            .. composition
                .Select(ImplementationOf)
                .OfType<Type>()
                .Where(type => platform(type) && IsConsumer(type))
                .Distinct()
                .OrderBy(Name, StringComparer.Ordinal)
        ];
    }

    /// <summary>The outbound clients registered, and the host's types that take or build one.</summary>
    public static IReadOnlyList<Type> Clients(IEnumerable<ServiceDescriptor> composition, Assembly host)
    {
        Func<Type, bool> platform = Platform(host);
        ServiceDescriptor[] descriptors = [.. composition];

        return
        [
            .. descriptors
                .Where(IsClientRegistration)
                .Select(descriptor => descriptor.ServiceType)
                .Concat(descriptors.Select(ImplementationOf).OfType<Type>().Where(t => platform(t) && HoldsAClient(t)))
                .Distinct()
                .OrderBy(Name, StringComparer.Ordinal)
        ];
    }

    /// <summary>Every consumer reaching an undeclared client or an untyped factory, and every declaration nothing
    /// reaches or cites.</summary>
    public static IReadOnlyList<string> Offenders(
        IEnumerable<ServiceDescriptor> composition,
        Assembly host,
        IEnumerable<ConsumerCallException> declared)
    {
        Graph graph = new([.. composition], Platform(host));
        ConsumerCallException[] exceptions = [.. declared];
        HashSet<Type> reached = [];
        List<string> offenders = [];

        foreach (Type consumer in Consumers(graph.Descriptors, host))
        {
            foreach ((Type client, string path) in graph.ClientsReachedFrom(consumer))
            {
                reached.Add(client);
                if (exceptions.All(exception => exception.Client != client))
                {
                    offenders.Add(
                        $"{Name(consumer)} reaches {Name(client)} ({path}) with no declared exception: a " +
                        "synchronous call inside a consumer is granted by an ADR (ADR-017, §9.7)");
                }
            }
        }

        offenders.AddRange(
            graph.Opaque
                .OrderBy(Name, StringComparer.Ordinal)
                .Select(service =>
                    $"{Name(service)} is built by a factory returning object, which a consumer reaches and the walk " +
                    "cannot see into: register it with its type"));

        foreach (ConsumerCallException exception in exceptions)
        {
            if (!reached.Contains(exception.Client))
                offenders.Add($"{Name(exception.Client)} is declared and no consumer reaches it, so it grants nothing");

            if (!CitesAnOwner().IsMatch(exception.GrantedBy))
                offenders.Add($"{Name(exception.Client)} is granted by '{exception.GrantedBy}', which names no ADR");

            if (string.IsNullOrWhiteSpace(exception.WhenUnreachable) ||
                string.IsNullOrWhiteSpace(exception.WhenAnsweredNo))
            {
                offenders.Add($"{Name(exception.Client)} does not say what follows unreachable and answered no");
            }
        }

        return offenders;
    }

    [GeneratedRegex(@"ADR-\d{3}")]
    private static partial Regex CitesAnOwner();

    // The host's own assemblies and the building blocks; a framework type is not descended into.
    private static Func<Type, bool> Platform(Assembly host)
    {
        string prefix = host.GetName().Name!.Split('.')[0] + ".";
        return type => type.Assembly.GetName().Name is { } name &&
            (name.StartsWith(prefix, StringComparison.Ordinal) || name.StartsWith("Common.", StringComparison.Ordinal));
    }

    private static bool IsConsumer(Type type) =>
        type.GetInterfaces().Any(i => DefinitionName(i) is "MassTransit.IConsumer" or "MassTransit.SagaStateMachine`1");

    // A typed client: the client factory's own registration builds it, so its descriptor's factory is declared there.
    private static bool IsClientRegistration(ServiceDescriptor descriptor) =>
        FactoryOf(descriptor)?.Method.DeclaringType?.Assembly.GetName().Name is { } factory &&
        ClientFactoryAssemblies.Contains(factory) &&
        !ClientFactoryAssemblies.Contains(descriptor.ServiceType.Assembly.GetName().Name);

    private static bool HoldsAClient(Type type) => TakesAClient(type) || BuildsAClient(type);

    // Fields and locals, the state machines' hoisted ones included, so an async method's client is seen.
    private static bool BuildsAClient(Type type)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.DeclaredOnly;

        IEnumerable<Type> locals = type
            .GetMethods(All)
            .Concat<MethodBase>(type.GetConstructors(All))
            .SelectMany(method => method.GetMethodBody()?.LocalVariables ?? [])
            .Select(local => local.LocalType);

        return type.GetFields(All).Select(field => field.FieldType).Concat(locals).Any(IsConstructedClient) ||
            type.GetNestedTypes(All).Any(BuildsAClient);
    }

    private static bool IsConstructedClient(Type type) =>
        ConstructedClientNames.Contains(type.FullName) ||
        BaseTypes(type).Any(b => ConstructedClientNames.Contains(b.FullName));

    private static bool TakesAClient(Type type) =>
        type
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => IsClientParameter(parameter.ParameterType));

    private static bool IsClientParameter(Type type) =>
        typeof(HttpMessageInvoker).IsAssignableFrom(type) ||
        ClientParameterNames.Contains(type.FullName) ||
        BaseTypes(type).Any(b => DefinitionName(b) == "Grpc.Core.ClientBase`1");

    private static IEnumerable<Type> BaseTypes(Type type)
    {
        for (Type? current = type.BaseType; current is not null; current = current.BaseType)
            yield return current;
    }

    private static string? DefinitionName(Type type) =>
        type.IsGenericType ? type.GetGenericTypeDefinition().FullName : type.FullName;

    private static Type? ImplementationOf(ServiceDescriptor descriptor) =>
        descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType;

    private static Delegate? FactoryOf(ServiceDescriptor descriptor) =>
        descriptor.IsKeyedService ? descriptor.KeyedImplementationFactory : descriptor.ImplementationFactory;

    private static object? InstanceOf(ServiceDescriptor descriptor) =>
        descriptor.IsKeyedService ? descriptor.KeyedImplementationInstance : descriptor.ImplementationInstance;

    private static string Name(Type type) =>
        type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<" +
              $"{string.Join(", ", type.GetGenericArguments().Select(Name))}>"
            : type.Name;

    private sealed class Graph(IReadOnlyList<ServiceDescriptor> descriptors, Func<Type, bool> platform)
    {
        private readonly ILookup<Type, ServiceDescriptor> _byService = descriptors.ToLookup(d => d.ServiceType);

        private readonly HashSet<Type> _clientServices =
            [.. descriptors.Where(IsClientRegistration).Select(d => d.ServiceType)];

        public IReadOnlyList<ServiceDescriptor> Descriptors { get; } = descriptors;

        /// <summary>The host's services a walk met built by a factory whose declared return is object.</summary>
        public HashSet<Type> Opaque { get; } = [];

        // Breadth first, so the path named for each client is a shortest one. A consumer's own client that no
        // registration shows is reported as the consumer's, and a registered one is left to the walk, under its type.
        public IEnumerable<(Type Client, string Path)> ClientsReachedFrom(Type consumer)
        {
            bool unregistered = BuildsAClient(consumer) || consumer
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => IsClientParameter(parameter.ParameterType) &&
                    !_clientServices.Contains(parameter.ParameterType));

            if (unregistered)
                yield return (consumer, $"{Name(consumer)} holds one itself");

            Dictionary<Type, Type?> parents = new() { [consumer] = null };
            Queue<Type> pending = new([consumer]);

            while (pending.TryDequeue(out Type? node))
            {
                if (node != consumer && (_clientServices.Contains(node) || HoldsAClient(node)))
                {
                    yield return (node, PathTo(node, parents));
                    continue;
                }

                foreach (Type next in Dependencies(node))
                {
                    if (parents.TryAdd(next, node))
                        pending.Enqueue(next);
                }
            }
        }

        private static string PathTo(Type node, Dictionary<Type, Type?> parents)
        {
            List<string> names = [];
            for (Type? current = node; current is not null; current = parents[current])
                names.Add(Name(current));

            names.Reverse();
            return string.Join(" -> ", names);
        }

        private IEnumerable<Type> Dependencies(Type node)
        {
            if (!platform(node))
                return [];

            return node
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .SelectMany(parameter => IsLocator(parameter.ParameterType)
                    ? LocatedHandlers()
                    : Targets(parameter.ParameterType, depth: 0));
        }

        private static bool IsLocator(Type type) =>
            type == typeof(IServiceProvider) || type == typeof(IServiceScopeFactory);

        // §6.2's handler families, which a dispatcher resolves by type at the call rather than by constructor.
        private IEnumerable<Type> LocatedHandlers() =>
            Descriptors
                .Where(d => d.ServiceType.IsGenericType &&
                    d.ServiceType.GetGenericTypeDefinition().Assembly == typeof(IDispatcher).Assembly)
                .SelectMany(d => Implementations(d, d.ServiceType, depth: 0));

        private IEnumerable<Type> Targets(Type service, int depth)
        {
            Type requested = service.IsGenericType && service.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? service.GetGenericArguments()[0]
                : service;

            IEnumerable<ServiceDescriptor> registrations = _byService[requested];
            if (!_byService.Contains(requested) && requested.IsGenericType)
                registrations = _byService[requested.GetGenericTypeDefinition()];

            return registrations.SelectMany(descriptor => Implementations(descriptor, requested, depth));
        }

        private IEnumerable<Type> Implementations(ServiceDescriptor descriptor, Type requested, int depth)
        {
            if (IsClientRegistration(descriptor))
                return [descriptor.ServiceType];

            if (ImplementationOf(descriptor) is { } implementation)
                return Closed(implementation, requested);

            if (InstanceOf(descriptor) is { } instance)
                return [instance.GetType()];

            // A factory is opaque; its declared return type is the best the descriptor says about what it builds.
            Type? returned = FactoryOf(descriptor)?.Method.ReturnType;
            if (returned == typeof(object) && platform(requested))
                Opaque.Add(requested);

            if (returned is null || returned == typeof(object) || depth > 4)
                return [];

            if (!returned.IsInterface && !returned.IsAbstract)
                return [returned];

            return returned == requested ? [] : Targets(returned, depth + 1);
        }

        private static IEnumerable<Type> Closed(Type implementation, Type requested)
        {
            if (!implementation.IsGenericTypeDefinition)
                return [implementation];

            if (!requested.IsConstructedGenericType || requested.ContainsGenericParameters)
                return [implementation];

            try
            {
                return [implementation.MakeGenericType(requested.GetGenericArguments())];
            }
            catch (ArgumentException)
            {
                // A constraint the arguments break, which the container answers by omitting the implementation.
                return [];
            }
        }
    }
}
