using System.Security.Claims;
using Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace WebApi.Authentication;

/// <summary>
/// The action needs a fresh second-factor confirmation ("step-up") on top of the access token: the caller sends the token that
/// <c>POST /api/account/step-up</c> gave them in <c>X-Elevation</c>, and it must belong to the same user as the access token.
/// Without it the answer is a 403 whose body says <c>"code": "step_up_required"</c>, which tells the caller to ask for a code
/// (and not that the user lacks a role). Put it on what can hurt: managing users, later the exchange keys and trading.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireElevationAttribute : Attribute, IAuthorizationFilter
{
    public const string Header = "X-Elevation";
    public const string ProblemCode = "step_up_required";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var elevated = context.HttpContext.RequestServices.GetRequiredService<IElevationTokenService>()
            .Validate(context.HttpContext.Request.Headers[Header].FirstOrDefault());
        var signedIn = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (elevated is { } userId && Guid.TryParse(signedIn, out var current) && userId == current)
            return;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Confirmation needed",
            Detail = "Confirm with a code from your authenticator app (or email) to do this."
        };
        problem.Extensions["code"] = ProblemCode;
        context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status403Forbidden };
    }
}
