using Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.ErrorHandling;

/// <summary>
/// Turns the domain's "you asked for something wrong" exceptions into 4xx answers, so they are not reported as a
/// server error with a stack trace. Anything else is left to the default handler and stays a plain 500.
/// </summary>
public sealed class DomainExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            ConfigurationValidationException => (StatusCodes.Status400BadRequest, "Invalid configuration"),
            ConfigurationConflictException => (StatusCodes.Status409Conflict, "Configuration conflict"),
            _ => (0, null)
        };

        if (status == 0)
            return false;

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = exception.Message }
        });
    }
}
