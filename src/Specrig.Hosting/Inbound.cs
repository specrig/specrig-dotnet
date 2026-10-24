using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Specrig;

sealed class Inbound(Trace trace, StandIns standIns) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(Record);
        next(app);
    };

    async Task Record(HttpContext context, RequestDelegate next)
    {
        Bypass.Current.Value = standIns;
        if (context.Request.ContentType?.StartsWith("application/grpc") == true)
        {
            await Grpc.Record(trace, context, next);
            return;
        }
        var call = trace.Begin();
        context.Request.EnableBuffering();
        var request = await new StreamReader(context.Request.Body, leaveOpen: true).ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;
        var answered = context.Response.Body;
        using var response = new MemoryStream();
        context.Response.Body = response;
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            context.Response.Body = answered;
            response.Position = 0;
            await response.CopyToAsync(answered);
            var status = failed ? 500 : context.Response.StatusCode;
            trace.End(call, "in", "http", Name(context), Body(request, context.Request.Query), status >= 400 ? Refusal(status, response.ToArray()) : null);
        }
    }

    // As the Go record names a route: parameters without their constraints, a catch-all as *.
    // An exception handler re-executes the request with no endpoint, and keeps the one it failed at.
    static string Name(HttpContext context)
    {
        if ((context.GetEndpoint() ?? context.Features.Get<IExceptionHandlerFeature>()?.Endpoint) is not RouteEndpoint endpoint)
        {
            return $"{context.Request.Method} {context.Request.Path}";
        }
        var segments = endpoint.RoutePattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternParameterPart { IsCatchAll: true } => "*",
            RoutePatternParameterPart parameter => $"{{{parameter.Name}}}",
            RoutePatternSeparatorPart separator => separator.Content,
            _ => "",
        })));
        return $"{context.Request.Method} /{string.Join('/', segments)}";
    }

    static JsonNode? Body(string request, IQueryCollection query)
    {
        if (request.Length > 0)
        {
            return Backend.Json(request);
        }
        if (query.Count == 0)
        {
            return null;
        }
        var values = new JsonObject();
        foreach (var (name, value) in query)
        {
            values[name] = new JsonArray([.. value.Select(v => JsonValue.Create(v))]);
        }
        return values;
    }

    // ASP.NET's own problem types are links to the RFC, which name no refusal.
    static string Refusal(int status, byte[] body)
    {
        var refusal = $"HTTP {status}";
        try
        {
            if (JsonNode.Parse(body) is JsonObject answer
                && answer["type"] is JsonValue type
                && type.TryGetValue<string>(out var name)
                && !Uri.IsWellFormedUriString(name, UriKind.Absolute))
            {
                return $"{refusal} {name}";
            }
        }
        catch (JsonException)
        {
        }
        return refusal;
    }
}
