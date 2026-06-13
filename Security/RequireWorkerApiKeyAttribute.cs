using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using VirtualSportsImporter.Worker.Options;

namespace VirtualSportsImporter.Worker.Security;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireWorkerApiKeyAttribute : Attribute, IAuthorizationFilter
{
    public const string HeaderName = "X-Worker-Api-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var options = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<WorkerSecurityOptions>>()
            .Value;

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            context.Result = Unauthorized();
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var providedApiKey) ||
            providedApiKey.Count != 1 ||
            !string.Equals(providedApiKey[0], options.ApiKey, StringComparison.Ordinal))
        {
            context.Result = Unauthorized();
        }
    }

    private static JsonResult Unauthorized()
    {
        return new JsonResult(new
        {
            success = false,
            errors = new[] { "Missing or invalid worker API key." }
        })
        {
            StatusCode = StatusCodes.Status401Unauthorized
        };
    }
}
