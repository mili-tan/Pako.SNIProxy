using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Pako.SNIProxy.Configuration;

namespace Pako.SNIProxy.Api;

public sealed class ApiAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _expectedToken;

    public ApiAuthMiddleware(RequestDelegate next, ProxyOptions options)
    {
        _next = next;
        _expectedToken = options.ManagementApi.AuthToken;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("Authorization", out StringValues authHeader))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing Authorization header." });
            return;
        }

        var value = authHeader.ToString();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(value[prefix.Length..].Trim(), _expectedToken, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid token." });
            return;
        }

        await _next(context);
    }
}
