using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Features.Escrows.Commands;
using EscrowMarketplace.Application.Features.Escrows.Queries;
using EscrowMarketplace.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace EscrowMarketplace.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EscrowsController : ControllerBase
{
    private readonly ISender _sender;

    public EscrowsController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet("booking/{bookingId:guid}")]
    [ProducesResponseType(typeof(EscrowDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EscrowDto>> GetByBookingId(
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetEscrowByBookingIdQuery(bookingId), cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/authorize")]
    [ProducesResponseType(typeof(EscrowDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<EscrowDto>> AuthorizeFunds(
        Guid id,
        [FromBody] AuthorizePaymentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new AuthorizeEscrowFundsCommand(id, request.PaymentReference), cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/fund")]
    [ProducesResponseType(typeof(EscrowDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<EscrowDto>> FundEscrow(
        Guid id,
        [FromQuery] int inspectionDays = 3,
        CancellationToken cancellationToken = default)
    {
        var result = await _sender.Send(new FundEscrowCommand(id, inspectionDays), cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/release")]
    [ProducesResponseType(typeof(EscrowDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<EscrowDto>> ReleasePayout(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new ReleaseEscrowPayoutCommand(id), cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/refund")]
    [ProducesResponseType(typeof(EscrowDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<EscrowDto>> RefundEscrow(
        Guid id,
        [FromBody] RefundRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new RefundEscrowCommand(id, request.Reason), cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/dispute")]
    [ProducesResponseType(typeof(DisputeDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DisputeDto>> RaiseDispute(
        Guid id,
        [FromBody] RaiseDisputeRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new RaiseDisputeCommand(id, request.RaisedByUserId, request.Reason, request.EvidenceSummary),
            cancellationToken);
        return Ok(result);
    }

    [HttpGet("disputes")]
    [ProducesResponseType(typeof(IReadOnlyList<DisputeDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DisputeDto>>> GetDisputes(CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetDisputesQuery(), cancellationToken);
        return Ok(result);
    }

    [HttpPost("disputes/{disputeId:guid}/resolve")]
    [ProducesResponseType(typeof(DisputeDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DisputeDto>> ResolveDispute(
        Guid disputeId,
        [FromBody] ResolveDisputeRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new ResolveDisputeCommand(disputeId, request.Resolution, request.ResolutionNotes),
            cancellationToken);
        return Ok(result);
    }
}

public record AuthorizePaymentRequest(string PaymentReference);
public record RefundRequest(string Reason);
public record RaiseDisputeRequest(Guid RaisedByUserId, string Reason, string EvidenceSummary);
public record ResolveDisputeRequest(DisputeStatus Resolution, string ResolutionNotes);
