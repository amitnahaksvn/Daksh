using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Daksh.Api.Infrastructure;

/// <summary>Maps exceptions to RFC 7807 problem details; internals are never leaked to clients.</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        ProblemDetails problem;
        if (ex is ValidationException ve)
        {
            problem = new ValidationProblemDetails(ve.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()))
            { Status = 400, Title = "Validation failed" };
        }
        else
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            problem = new ProblemDetails { Status = 500, Title = "An unexpected error occurred" };
        }
        problem.Extensions["traceId"] = ctx.TraceIdentifier;
        ctx.Response.StatusCode = problem.Status!.Value;
        await ctx.Response.WriteAsJsonAsync(problem, problem.GetType(), cancellationToken: ct);
        return true;
    }
}

/// <summary>Endpoint filter running the FluentValidation validator for TRequest, if the argument is present.</summary>
public sealed class ValidationFilter<T> : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var arg = ctx.Arguments.OfType<T>().FirstOrDefault();
        if (arg is null)
            return Results.Problem(statusCode: 400, title: "Request body is required");
        var validator = ctx.HttpContext.RequestServices.GetRequiredService<IValidator<T>>();
        var result = await validator.ValidateAsync(arg, ctx.HttpContext.RequestAborted);
        if (!result.IsValid)
            throw new ValidationException(result.Errors);
        return await next(ctx);
    }
}

public static class ValidationExtensions
{
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder b) where T : class =>
        b.AddEndpointFilter<ValidationFilter<T>>();
}
