using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Api;

/// <summary>
/// The user of the current HTTP request, or the user set explicitly for
/// background work (<see cref="ICurrentUserOverride"/>).
/// </summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser, ICurrentUserOverride
{
    private Guid? _explicit;

    public Guid? UserId => _explicit
        ?? (Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(PaperDotNetClaims.UserId), out var id) ? id : null);

    public void ActAs(Guid userId) => _explicit = userId;
}
