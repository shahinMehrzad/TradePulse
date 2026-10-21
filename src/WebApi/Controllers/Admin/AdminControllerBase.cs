using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Authentication;

namespace WebApi.Controllers.Admin;

/// <summary>
/// Base for the admin panel's controllers. Inheriting it is what makes a controller admin-only: the policy and
/// <c>[ApiController]</c> come along, so a new management controller cannot go live unprotected by forgetting an attribute.
/// The route defaults to <c>api/admin/[controller]</c>; a controller that needs another one declares its own <c>[Route]</c>.
/// </summary>
[Route("api/admin/[controller]")]
[ApiController]
[Authorize(Policy = JwtAuthenticationExtensions.AdminPolicy)]
public abstract class AdminControllerBase : BaseController
{
}
