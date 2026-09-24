using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Api;

/// <summary>The user of the current HTTP request, or none for background work.</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(PaperDotNetClaims.UserId), out var id) ? id : null;
}
