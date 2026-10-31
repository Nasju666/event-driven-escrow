using EscrowMarketplace.Api.Authentication;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Features.Users;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EscrowMarketplace.Api.Controllers;

/// <summary>The signed-in user's profile and wallet balance.</summary>
[ApiController]
[Route("api/me")]
[EnableRateLimiting(RateLimitPolicies.PerUser)]
public class MeController : ControllerBase
{
    private readonly ISender _sender;

    public MeController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    public async Task<ActionResult<CurrentUserDto>> Get(CancellationToken ct)
        => Ok(await _sender.Send(new GetCurrentUserQuery(), ct));
}
