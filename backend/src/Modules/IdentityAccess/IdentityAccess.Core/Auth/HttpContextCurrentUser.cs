using System.Security.Claims;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace IdentityAccess.Core.Auth;

/// <summary>
/// <see cref="ICurrentUser"/> поверх <see cref="IHttpContextAccessor"/>: идентификатор из claim
/// <c>sub</c>, права — из claim <c>permission</c> (их кладёт <see cref="TokenService"/> в JWT).
/// </summary>
internal sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    /// <summary>Тип claim, в который <see cref="TokenService"/> пишет права роли.</summary>
    internal const string PermissionClaimType = "permission";

    private const string SubjectClaimType = "sub";

    public Guid? UserId
    {
        get
        {
            var value = Principal?.FindFirstValue(SubjectClaimType);

            return Guid.TryParse(value, out var userId) ? userId : null;
        }
    }

    public IReadOnlyCollection<string> Permissions =>
        Principal?
            .FindAll(PermissionClaimType)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray()
        ?? [];

    public string? Ip => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent
    {
        get
        {
            var context = accessor.HttpContext;
            if (context is null)
            {
                return null;
            }

            var userAgent = context.Request.Headers.UserAgent;

            return StringValues.IsNullOrEmpty(userAgent) ? null : userAgent.ToString();
        }
    }

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool Has(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        return Principal?.HasClaim(PermissionClaimType, permission) ?? false;
    }
}
