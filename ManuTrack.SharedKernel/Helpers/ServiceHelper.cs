using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace ManuTrack.SharedKernel.Helpers;

public static class ServiceHelper
{
    /// <summary>Extracts the raw JWT token from the Authorization header.</summary>
    public static string? GetBearerToken(IHttpContextAccessor accessor)
    {
        var auth = accessor.HttpContext?.Request.Headers["Authorization"].ToString();
        return auth?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? auth["Bearer ".Length..] : null;
    }

    /// <summary>Returns (UserId, UserName) from the current HTTP context claims.</summary>
    public static (int UserId, string UserName) GetCurrentUser(IHttpContextAccessor accessor)
    {
        var user = accessor.HttpContext?.User;
        var idVal = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? user?.FindFirst("sub")?.Value;
        var name = user?.FindFirst(ClaimTypes.Name)?.Value
                ?? user?.FindFirst("name")?.Value
                ?? "Unknown";
        int.TryParse(idVal, out var id);
        return (id, name);
    }

    /// <summary>
    /// Creates a named HttpClient and attaches the current request's Bearer token
    /// to its Authorization header automatically.
    /// </summary>
    public static HttpClient CreateAuthorizedClient(
        IHttpClientFactory factory,
        IHttpContextAccessor accessor,
        string clientName)
    {
        var client = factory.CreateClient(clientName);
        var token = GetBearerToken(accessor);
        if (token != null)
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
