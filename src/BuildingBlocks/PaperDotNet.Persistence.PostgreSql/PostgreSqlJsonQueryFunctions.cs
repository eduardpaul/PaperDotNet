using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Persistence.PostgreSql;

/// <summary>jsonb operators via Npgsql's EF functions: <c>@&gt;</c> and <c>?</c>.</summary>
internal sealed class PostgreSqlJsonQueryFunctions : IJsonQueryFunctions
{
    private static readonly MethodInfo JsonContainsMethod = typeof(NpgsqlJsonDbFunctionsExtensions)
        .GetMethod(nameof(NpgsqlJsonDbFunctionsExtensions.JsonContains), [typeof(DbFunctions), typeof(object), typeof(object)])!;

    private static readonly MethodInfo JsonExistsMethod = typeof(NpgsqlJsonDbFunctionsExtensions)
        .GetMethod(nameof(NpgsqlJsonDbFunctionsExtensions.JsonExists), [typeof(DbFunctions), typeof(object), typeof(string)])!;

    private static readonly Expression Functions = Expression.Constant(EF.Functions);

    public Expression Contains(Expression document, string jsonFragment) =>
        Expression.Call(JsonContainsMethod, Functions, Expression.Convert(document, typeof(object)), Expression.Constant(jsonFragment, typeof(object)));

    public Expression HasProperty(Expression document, string propertyName) =>
        Expression.Call(JsonExistsMethod, Functions, Expression.Convert(document, typeof(object)), Expression.Constant(propertyName));
}
