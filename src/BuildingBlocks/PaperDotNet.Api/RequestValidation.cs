using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;

namespace PaperDotNet.Api;

/// <summary>DataAnnotations validation for request DTOs, returning a Graph-style validation problem.</summary>
public static class RequestValidation
{
    public static ValidationProblem? Validate(object request)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
        {
            return null;
        }

        var errors = results
            .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : [string.Empty]).Select(m => (Member: m, r.ErrorMessage)))
            .GroupBy(x => ToCamelCase(x.Member))
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid value.").ToArray());
        return ApiErrors.Validation(errors);
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
