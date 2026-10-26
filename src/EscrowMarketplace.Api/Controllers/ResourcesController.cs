using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Features.Resources.Commands;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace EscrowMarketplace.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ResourcesController : ControllerBase
{
    private readonly ISender _sender;

    public ResourcesController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ResourceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ResourceDto>>> GetResources(
        [FromQuery] bool onlyActive = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _sender.Send(new GetResourcesQuery(onlyActive), cancellationToken);
        return Ok(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(ResourceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ResourceDto>> CreateResource(
        [FromBody] CreateResourceCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(command, cancellationToken);
        return CreatedAtAction(nameof(GetResources), new { id = result.Id }, result);
    }
}
