using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Authentication;

namespace WebApi.Controllers;

/// <summary>
/// Base for the admin panel's controllers. Inheriting it is what makes a controller admin-only: the policy and
/// <c>[ApiController]</c> come along, so a new management controller cannot go live unprotected by forgetting an attribute.
/// Each controller still declares its own route, e.g. <c>[Route("api/admin/worker-configurations")]</c>.
/// </summary>
[ApiController]
[Authorize(Policy = JwtAuthenticationExtensions.AdminPolicy)]
public abstract class AdminControllerBase : BaseController
{
}
