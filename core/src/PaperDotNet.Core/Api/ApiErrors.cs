using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace PaperDotNet.Core.Api;

/// <summary>Problem details with a stable <c>code</c>, as in the rest of the API.</summary>
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

    public static ProblemHttpResult BadRequest(string code, string detail) =>
        Problem(StatusCodes.Status400BadRequest, code, detail);

    public static ProblemHttpResult PreconditionRequired() =>
        Problem(StatusCodes.Status428PreconditionRequired, "preconditionRequired", "An If-Match header with the current ETag is required.");

    public static ProblemHttpResult PreconditionFailed() =>
        Problem(StatusCodes.Status412PreconditionFailed, "preconditionFailed", "The resource was changed by someone else. Reload it and try again.");

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(errors, extensions: new Dictionary<string, object?> { ["code"] = "invalidRequest" });

    public static ValidationProblem Validation(string field, string error) =>
        Validation(new Dictionary<string, string[]> { [field] = [error] });
}
