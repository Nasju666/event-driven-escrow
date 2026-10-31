using EscrowMarketplace.Api.Authentication;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Features.Escrows.Commands;
using EscrowMarketplace.Application.Features.Escrows.Queries;
using EscrowMarketplace.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EscrowMarketplace.Api.Controllers;

/// <summary>
/// Escrow ledger and dispute arbitration. Money only moves through booking actions or an admin's
/// dispute resolution; there are no raw "release" or "refund" endpoints anyone can call.
/// </summary>
[ApiController]
[Route("api/escrows")]
[EnableRateLimiting(RateLimitPolicies.PerUser)]
public class EscrowsController : ControllerBase
{
    private readonly ISender _sender;

    public EscrowsController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Escrow account and its ledger entries, for the booking's buyer, provider or an admin.</summary>
    [HttpGet("booking/{bookingId:guid}")]
    [ProducesResponseType(typeof(EscrowDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EscrowDto>> GetByBookingId(Guid bookingId, CancellationToken ct)
        => Ok(await _sender.Send(new GetEscrowByBookingIdQuery(bookingId), ct));

    /// <summary>Admins see all disputes in their tenant; buyers and providers see their own.</summary>
    [HttpGet("disputes")]
    public async Task<ActionResult<IReadOnlyList<DisputeDto>>> GetDisputes([FromQuery] bool openOnly = false, CancellationToken ct = default)
        => Ok(await _sender.Send(new GetDisputesQuery(openOnly), ct));

    [HttpPost("disputes/{disputeId:guid}/resolve")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<DisputeDto>> ResolveDispute(Guid disputeId, [FromBody] ResolveDisputeRequest request, CancellationToken ct)
        => Ok(await _sender.Send(new ResolveDisputeCommand(disputeId, request.Resolution, request.ResolutionNotes), ct));
}

public record ResolveDisputeRequest(DisputeStatus Resolution, string ResolutionNotes);
