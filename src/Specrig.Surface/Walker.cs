using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Specrig;

sealed partial class Walker(Compilation compilation, string service)
{
    const int Deepest = 12;
    const string Uncalled = "\u0000";

    static readonly Dictionary<string, string> mapped = new()
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapPatch"] = "PATCH", ["MapDelete"] = "DELETE",
    };

    static readonly Dictionary<string, string> requests = new()
    {
        ["GetAsync"] = "GET", ["GetStringAsync"] = "GET", ["GetByteArrayAsync"] = "GET", ["GetStreamAsync"] = "GET",
        ["GetFromJsonAsync"] = "GET", ["GetFromJsonAsAsyncEnumerable"] = "GET",
        ["PostAsync"] = "POST", ["PostAsJsonAsync"] = "POST",
        ["PutAsync"] = "PUT", ["PutAsJsonAsync"] = "PUT",
        ["PatchAsync"] = "PATCH", ["PatchAsJsonAsync"] = "PATCH",
        ["DeleteAsync"] = "DELETE", ["DeleteFromJsonAsync"] = "DELETE",
    };

    readonly Dictionary<IMethodSymbol, List<IInvocationOperation>> callers = new(SymbolEqualityComparer.Default);
    readonly List<IInvocationOperation> invocations = [];
    readonly List<IPropertyReferenceOperation> indexers = [];
    readonly List<INamedTypeSymbol> types = [];
    readonly Surface surface = new() { Service = service };

    public Surface Run()
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax when model.GetOperation(node) is IInvocationOperation invocation:
                        invocations.Add(invocation);
                        var target = invocation.TargetMethod.OriginalDefinition;
                        (callers.TryGetValue(target, out var list) ? list : callers[target] = []).Add(invocation);
                        break;
                    case ElementAccessExpressionSyntax when model.GetOperation(node) is IPropertyReferenceOperation { Property.IsIndexer: true } indexer:
                        indexers.Add(indexer);
                        break;
                    case TypeDeclarationSyntax when model.GetDeclaredSymbol(node) is INamedTypeSymbol type:
                        types.Add(type);
                        break;
                }
            }
        }
        invocations.Where(i => !Generated(i.Syntax.SyntaxTree)).ToList().ForEach(Classify);
        indexers.ForEach(Indexed);
        types.ForEach(Declared);
        return surface;
    }

    void Classify(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        var owner = method.ContainingType;
        IOperation? Argument(string name) => invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == name)?.Value;

        if (mapped.TryGetValue(method.Name, out var verb) && Is(owner, "Microsoft.AspNetCore.Builder"))
        {
            Serve([verb], Argument("endpoints"), Argument("pattern"));
        }
        else if (method.Name == "MapMethods" && Is(owner, "Microsoft.AspNetCore.Builder"))
        {
            Serve(Strings(Argument("httpMethods")), Argument("endpoints"), Argument("pattern"));
        }
        else if (method.Name == "MapGrpcService" && method.TypeArguments is [INamedTypeSymbol implementation])
        {
            ServeGrpc(implementation);
        }
        else if (requests.TryGetValue(method.Name, out verb) && (IsHttpClient(owner) || Is(owner, "System.Net.Http.Json")))
        {
            Request(verb, invocation.Instance ?? Argument("client"), Argument("requestUri"));
        }
        else if (method.Name == "SendAsync" && IsHttpClient(owner) && Unwrapped(Argument("request")) is IObjectCreationOperation created)
        {
            var arguments = created.Arguments.ToDictionary(a => a.Parameter?.Name ?? "", a => a.Value);
            if (arguments.TryGetValue("method", out var named) && Unwrapped(named) is IPropertyReferenceOperation property)
            {
                Request(property.Property.Name.ToUpperInvariant(), invocation.Instance, arguments.GetValueOrDefault("requestUri"));
            }
        }
        else if (GrpcService(owner) is { } grpc && SymbolEqualityComparer.Default.Equals(method.ContainingType, owner) && method.MethodKind == MethodKind.Ordinary)
        {
            surface.Calls.Add($"/{grpc}/{(method.Name.EndsWith("Async") ? method.Name[..^5] : method.Name)}");
            surface.Descriptors[grpc] = owner!.ContainingType!.ToDisplayString();
        }
        else if (method.Name == "BasicPublishAsync" && Is(owner, "RabbitMQ.Client"))
        {
            surface.Publishes.UnionWith(Named(Argument("routingKey"), invocation, "publishes"));
        }
        else if (method.Name == "QueueBindAsync" && Is(owner, "RabbitMQ.Client"))
        {
            surface.Consumes.UnionWith(Named(Argument("routingKey"), invocation, "consumes"));
            var enclosing = invocation.SemanticModel?.GetEnclosingSymbol(invocation.Syntax.SpanStart);
            for (var type = enclosing?.ContainingType; type is not null; type = type.ContainingType)
            {
                if (Implements(type, "Microsoft.Extensions.Hosting.IHostedService"))
                {
                    surface.Consumers.Add(type.ToDisplayString());
                }
            }
        }
        else if (method.Name == "GetConnectionString" && Is(owner, "Microsoft.Extensions.Configuration"))
        {
            surface.Settings.UnionWith(Constants(Argument("name")).Select(name => $"ConnectionStrings:{name}"));
        }
        else if (method.Name == "GetValue" && Is(owner, "Microsoft.Extensions.Configuration"))
        {
            var keys = Constants(Argument("key")).SelectMany(key => Under(Argument("configuration"), key));
            (Argument("defaultValue") is null ? surface.Settings : surface.Optional).UnionWith(keys);
        }
        else if (method.Name is "GetSection" or "GetRequiredSection" or "BindConfiguration" && Is(owner, "Microsoft.Extensions"))
        {
            var key = Argument("key") ?? Argument("configSectionPath");
            surface.Sections.UnionWith(Constants(key).SelectMany(k => Under(invocation.Instance ?? Argument("configuration"), k)));
        }
    }

    void Indexed(IPropertyReferenceOperation indexer)
    {
        if (Implements(indexer.Property.ContainingType, "Microsoft.Extensions.Configuration.IConfiguration") && indexer.Arguments is [var key])
        {
            surface.Settings.UnionWith(Constants(key.Value).SelectMany(k => Under(indexer.Instance, k)));
        }
    }

    void Declared(INamedTypeSymbol type)
    {
        if (Derives(type, "Microsoft.AspNetCore.Mvc.ControllerBase") && !type.IsAbstract)
        {
            Controller(type);
        }
        if (type.TypeKind == TypeKind.Interface)
        {
            Refit(type);
        }
    }

    void Serve(IEnumerable<string> verbs, IOperation? endpoints, IOperation? pattern)
    {
        foreach (var prefix in Prefixes(endpoints, 0))
        {
            foreach (var route in Templates(pattern, 0))
            {
                foreach (var verb in verbs)
                {
                    surface.Serves.Add($"{verb} {Route(prefix + "/" + route)}");
                }
            }
        }
    }

    IEnumerable<string> Prefixes(IOperation? endpoints, int depth)
    {
        switch (Unwrapped(endpoints))
        {
            case IInvocationOperation { TargetMethod.Name: "MapGroup" } group when depth < Deepest:
                var arguments = group.Arguments.ToDictionary(a => a.Parameter?.Name ?? "", a => a.Value);
                return Prefixes(arguments.GetValueOrDefault("endpoints"), depth + 1)
                    .SelectMany(outer => Templates(arguments.GetValueOrDefault("prefix"), 0).Select(inner => $"{outer}/{inner}"));
            case ILocalReferenceOperation local when Initializer(local.Local) is { } value:
                return Prefixes(value, depth + 1);
            case IParameterReferenceOperation parameter when Callers(parameter.Parameter) is { Count: > 0 } calls:
                return calls.SelectMany(argument => Prefixes(argument, depth + 1));
            default:
                return [""];
        }
    }

    void ServeGrpc(INamedTypeSymbol implementation)
    {
        for (var type = implementation.BaseType; type is not null; type = type.BaseType)
        {
            if (ServiceName(type.ContainingType) is { } name)
            {
                foreach (var method in implementation.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsOverride))
                {
                    if (SymbolEqualityComparer.Default.Equals(method.OverriddenMethod?.ContainingType, type))
                    {
                        surface.Serves.Add($"/{name}/{method.Name}");
                    }
                }
                return;
            }
        }
    }

    void Request(string verb, IOperation? client, IOperation? uri)
    {
        var key = ClientKey(client);
        foreach (var template in Templates(uri, 0))
        {
            if (template.StartsWith(Uncalled, StringComparison.Ordinal))
            {
                continue;
            }
            var request = $"{verb} {Path(template.Replace(Uncalled, "{}"))}";
            surface.Calls.Add(request);
            if (key is not null)
            {
                (surface.Clients.TryGetValue(key, out var set) ? set : surface.Clients[key] = new(StringComparer.Ordinal)).Add(request);
            }
        }
    }

    void Controller(INamedTypeSymbol type)
    {
        var name = type.Name.EndsWith("Controller") ? type.Name[..^10] : type.Name;
        var prefixes = Ancestry(type).SelectMany(t => t.GetAttributes()).Where(a => Mvc(a, "RouteAttribute")).Select(a => Template(a) ?? "").ToList();
        IEnumerable<string> Combined(string? template)
        {
            if (template is not null && (template.StartsWith('/') || template.StartsWith("~/")))
            {
                return [template.TrimStart('~')];
            }
            if (prefixes.Count == 0)
            {
                return template is null ? [] : [template];
            }
            return prefixes.Select(prefix => template is null ? prefix : $"{prefix}/{template}");
        }
        foreach (var action in Actions(type))
        {
            var attributes = Overridden(action).SelectMany(m => m.GetAttributes()).ToList();
            var routes = attributes.Where(a => Mvc(a, "RouteAttribute")).Select(a => Template(a) ?? "").ToList();
            var verbs = attributes.Where(a => a.AttributeClass is { Name: var n } c && n.StartsWith("Http") && Is(c, "Microsoft.AspNetCore.Mvc"))
                .Select(a => (Verb: a.AttributeClass!.Name[4..^9].ToUpperInvariant(), Template: Template(a))).ToList();
            var silent = verbs.Where(v => v.Template is null).Select(v => v.Verb).ToList();
            var selectors = verbs.Where(v => v.Template is not null).ToList();
            selectors.AddRange(routes.Count == 0
                ? silent.Select(verb => (verb, (string?)null))
                : routes.SelectMany(route => silent.Select(verb => (verb, (string?)route))));
            foreach (var (verb, template) in selectors)
            {
                foreach (var route in Combined(template))
                {
                    surface.Serves.Add($"{verb} {Route(route.Replace("[controller]", name).Replace("[action]", action.Name))}");
                }
            }
        }
    }

    static IEnumerable<IMethodSymbol> Actions(INamedTypeSymbol type)
    {
        var overridden = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var declaring in Ancestry(type))
        {
            foreach (var method in declaring.GetMembers().OfType<IMethodSymbol>())
            {
                if (method is { MethodKind: MethodKind.Ordinary, DeclaredAccessibility: Accessibility.Public, IsStatic: false }
                    && !overridden.Contains(method)
                    && !Overridden(method).SelectMany(m => m.GetAttributes()).Any(a => Mvc(a, "NonActionAttribute")))
                {
                    yield return method;
                }
                overridden.UnionWith(Overridden(method).Skip(1));
            }
        }
    }

    static IEnumerable<INamedTypeSymbol> Ancestry(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? at = type; at is not null && !Is(at, "Microsoft.AspNetCore.Mvc"); at = at.BaseType)
        {
            yield return at;
        }
    }

    static IEnumerable<IMethodSymbol> Overridden(IMethodSymbol method)
    {
        for (IMethodSymbol? at = method; at is not null; at = at.OverriddenMethod)
        {
            yield return at;
        }
    }

    static bool Mvc(AttributeData attribute, string name) => attribute.AttributeClass is { } type && type.Name == name && Is(type, "Microsoft.AspNetCore.Mvc");

    static string? Template(AttributeData attribute) => attribute.ConstructorArguments is [{ Value: string template }, ..] ? template : null;

    void Refit(INamedTypeSymbol type)
    {
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            foreach (var attribute in method.GetAttributes().Where(a => Is(a.AttributeClass, "Refit") && a.AttributeClass!.Name.EndsWith("Attribute")))
            {
                var verb = attribute.AttributeClass!.Name[..^9].ToUpperInvariant();
                if (verb is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" && attribute.ConstructorArguments.FirstOrDefault().Value is string path)
                {
                    var request = $"{verb} {Path(Placeholders().Replace(path, "{}"))}";
                    surface.Calls.Add(request);
                    var key = type.ToDisplayString();
                    (surface.Clients.TryGetValue(key, out var set) ? set : surface.Clients[key] = new(StringComparer.Ordinal)).Add(request);
                }
            }
        }
    }

    IEnumerable<string> Named(IOperation? argument, IOperation at, string kind)
    {
        foreach (var name in Templates(argument, 0).Where(t => !t.StartsWith(Uncalled, StringComparison.Ordinal)))
        {
            if (name.Contains("{}") || name.Contains(Uncalled))
            {
                var where = at.Syntax.GetLocation().GetLineSpan();
                throw new InvalidOperationException(
                    $"{where.Path}:{where.StartLinePosition.Line + 1}: {kind} a name that is not a constant — it is a contract, give it one");
            }
            yield return name;
        }
    }

    IEnumerable<string> Constants(IOperation? key) => Templates(key, 0).Where(k => !k.Contains("{}") && !k.Contains(Uncalled));

    IEnumerable<string> Under(IOperation? configuration, string key) => Sections(configuration, 0).Select(section => section + key);

    IEnumerable<string> Sections(IOperation? configuration, int depth) => Unwrapped(configuration) switch
    {
        IInvocationOperation { TargetMethod.Name: "GetSection" or "GetRequiredSection" } section when depth < Deepest =>
            Constants(section.Arguments.FirstOrDefault(a => a.Parameter?.Name == "key")?.Value)
                .SelectMany(key => Sections(section.Instance ?? section.Arguments.FirstOrDefault()?.Value, depth + 1).Select(outer => $"{outer}{key}:")),
        ILocalReferenceOperation local when Initializer(local.Local) is { } value => Sections(value, depth + 1),
        _ => [""],
    };

    IReadOnlyList<string> Templates(IOperation? operation, int depth)
    {
        operation = Unwrapped(operation);
        if (operation is null || depth > Deepest)
        {
            return ["{}"];
        }
        if (operation.ConstantValue is { HasValue: true, Value: var constant })
        {
            return constant is string text ? [text] : [];
        }
        switch (operation)
        {
            case IInterpolatedStringOperation interpolated:
                IReadOnlyList<string> parts = [""];
                foreach (var part in interpolated.Parts)
                {
                    parts = Product(parts, part switch
                    {
                        IInterpolatedStringTextOperation text => Templates(text.Text, depth + 1),
                        IInterpolationOperation hole => Templates(hole.Expression, depth + 1),
                        _ => ["{}"],
                    });
                }
                return parts;
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } sum:
                return Product(Templates(sum.LeftOperand, depth + 1), Templates(sum.RightOperand, depth + 1));
            case IConditionalOperation conditional:
                return [.. Templates(conditional.WhenTrue, depth + 1), .. Templates(conditional.WhenFalse, depth + 1)];
            case ICoalesceOperation coalesce:
                return [.. Templates(coalesce.Value, depth + 1), .. Templates(coalesce.WhenNull, depth + 1)];
            case ILocalReferenceOperation local when Initializer(local.Local) is { } value:
                return Templates(value, depth + 1);
            case IFieldReferenceOperation field when Initializer(field.Field) is { } value:
                return Templates(value, depth + 1);
            case IParameterReferenceOperation parameter when parameter.Parameter.ContainingSymbol is IMethodSymbol { MethodKind: not MethodKind.AnonymousFunction }:
                var calls = Callers(parameter.Parameter);
                return calls.Count == 0 ? [Uncalled] : [.. calls.SelectMany(argument => Templates(argument, depth + 1))];
            default:
                return ["{}"];
        }
    }

    List<IOperation> Callers(IParameterSymbol parameter)
    {
        if (parameter.ContainingSymbol is not IMethodSymbol method || !callers.TryGetValue(method.OriginalDefinition, out var calls))
        {
            return [];
        }
        return [.. calls.Select(call => call.Arguments.FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.Parameter?.OriginalDefinition, parameter.OriginalDefinition))?.Value)
            .OfType<IOperation>()];
    }

    IOperation? Initializer(ISymbol symbol)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } value })
            {
                return compilation.GetSemanticModel(value.SyntaxTree).GetOperation(value);
            }
        }
        return null;
    }

    string? ClientKey(IOperation? client) => Unwrapped(client) switch
    {
        IFieldReferenceOperation field => field.Field.ContainingType.ToDisplayString(),
        IPropertyReferenceOperation property => property.Property.ContainingType.ToDisplayString(),
        IParameterReferenceOperation parameter => parameter.Parameter.ContainingType?.ToDisplayString(),
        _ => null,
    };

    static string? GrpcService(INamedTypeSymbol? client) =>
        client is not null && Derives(client, "Grpc.Core.ClientBase") ? ServiceName(client.ContainingType) : null;

    static string? ServiceName(INamedTypeSymbol? generated) =>
        generated?.GetMembers("__ServiceName").OfType<IFieldSymbol>().FirstOrDefault() is { } field
        && field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: LiteralExpressionSyntax literal }
            ? literal.Token.ValueText
            : null;

    static bool Generated(SyntaxTree tree) =>
        tree.FilePath.Split('/', '\\').Contains("obj") || tree.FilePath.EndsWith(".g.cs", StringComparison.Ordinal);

    static IOperation? Unwrapped(IOperation? operation)
    {
        while (operation is IConversionOperation or IParenthesizedOperation or IArgumentOperation)
        {
            operation = operation switch
            {
                IConversionOperation conversion => conversion.Operand,
                IParenthesizedOperation parenthesized => parenthesized.Operand,
                IArgumentOperation argument => argument.Value,
                _ => operation,
            };
        }
        return operation;
    }

    IEnumerable<string> Strings(IOperation? array) => Unwrapped(array) switch
    {
        IArrayCreationOperation { Initializer: { } values } => values.ElementValues.SelectMany(v => Constants(v)),
        ICollectionExpressionOperation collection => collection.Elements.SelectMany(v => Constants(v)),
        _ => [],
    };

    static IReadOnlyList<string> Product(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        [.. left.SelectMany(l => right.Select(r => l + r))];

    static bool Is(INamedTypeSymbol? type, string ns) =>
        type?.ContainingNamespace?.ToDisplayString() is { } name && (name == ns || name.StartsWith(ns + "."));

    static bool IsHttpClient(INamedTypeSymbol? type) => Derives(type, "System.Net.Http.HttpClient");

    static bool Derives(INamedTypeSymbol? type, string full)
    {
        for (; type is not null; type = type.BaseType)
        {
            if ((type.IsGenericType ? type.ConstructedFrom : type).ToDisplayString().Split('<')[0] == full)
            {
                return true;
            }
        }
        return false;
    }

    static bool Implements(INamedTypeSymbol? type, string full) =>
        type is not null && (type.ToDisplayString() == full || type.AllInterfaces.Any(i => i.ToDisplayString() == full));

    // As the Go record writes a route: parameters without their constraints, a catch-all as *.
    static string Route(string template)
    {
        var route = CatchAll().Replace(Parameter().Replace(template, "{$1}"), "*");
        return "/" + string.Join('/', route.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    static string Path(string template)
    {
        var route = template.Split('?')[0];
        if (Absolute().Match(route) is { Success: true } absolute)
        {
            route = absolute.Groups[1].Value;
        }
        if (route.Replace("/", "") is "{}" or "")
        {
            return route == "/" ? "/" : "external";
        }
        return "/" + string.Join('/', route.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Contains("{}") ? "{}" : s));
    }

    [GeneratedRegex(@"\{\*{1,2}[^}]*\}")]
    private static partial Regex CatchAll();

    [GeneratedRegex(@"\{([^}*:=?]+)[^}]*\}")]
    private static partial Regex Parameter();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Placeholders();

    [GeneratedRegex(@"^[a-z]+://[^/]*(/.*)?$")]
    private static partial Regex Absolute();
}
