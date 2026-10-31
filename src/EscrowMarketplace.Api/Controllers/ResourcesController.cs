using EscrowMarketplace.Api.Authentication;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Features.Resources.Commands;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EscrowMarketplace.Api.Controllers;

[ApiController]
[Route("api/resources")]
[EnableRateLimiting(RateLimitPolicies.PerUser)]
public class ResourcesController : ControllerBase
{
    private readonly ISender _sender;

    public ResourcesController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ResourceDto>>> List([FromQuery] bool onlyActive = true, CancellationToken ct = default)
        => Ok(await _sender.Send(new GetResourcesQuery(onlyActive), ct));

    [HttpPost]
    [Authorize(Policy = Policies.Provider)]
    [ProducesResponseType(typeof(ResourceDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<ResourceDto>> Create([FromBody] CreateResourceCommand command, CancellationToken ct)
    {
        var result = await _sender.Send(command, ct);
        return Created($"/api/resources/{result.Id}", result);
    }
}
