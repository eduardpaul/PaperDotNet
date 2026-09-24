using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace PaperDotNet.Api;

/// <summary>
/// RFC 9457 problem responses with a Graph-style machine-readable <c>code</c>.
/// </summary>
public static class ApiErrors
{
    public static ProblemHttpResult Problem(int status, string code, string detail) =>
        TypedResults.Problem(
            statusCode: status,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static ProblemHttpResult NotFound(string detail = "The resource was not found.") =>
        Problem(StatusCodes.Status404NotFound, "itemNotFound", detail);

    public static ProblemHttpResult Conflict(string code, string detail) =>
        Problem(StatusCodes.Status409Conflict, code, detail);

    public static ProblemHttpResult PreconditionRequired() =>
        Problem(StatusCodes.Status428PreconditionRequired, "preconditionRequired", "An If-Match header with the current ETag is required.");

    public static ProblemHttpResult PreconditionFailed() =>
        Problem(StatusCodes.Status412PreconditionFailed, "preconditionFailed", "The resource was changed by someone else. Reload it and try again.");

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(errors, extensions: new Dictionary<string, object?> { ["code"] = "invalidRequest" });
}
