using EscrowMarketplace.Api.Authentication;
using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Features.Bookings.Commands;
using EscrowMarketplace.Application.Features.Bookings.Queries;
using EscrowMarketplace.Application.Features.Escrows.Commands;
using EscrowMarketplace.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EscrowMarketplace.Api.Controllers;

/// <summary>
/// Booking lifecycle. Roles are enforced here; "is this your booking" is enforced in the handlers.
/// Identity always comes from the token, never from the request body.
///
///   buyer:    POST /bookings → POST /{id}/pay → (after completion) POST /{id}/approve or /{id}/dispute
///   provider: POST /{id}/confirm → POST /{id}/complete
///   either:   POST /{id}/cancel (full refund if paid)
/// </summary>
[ApiController]
[Route("api/bookings")]
[EnableRateLimiting(RateLimitPolicies.PerUser)]
public class BookingsController : ControllerBase
{
    private readonly ISender _sender;

    public BookingsController(ISender sender)
    {
        _sender = sender;
    }

    [HttpPost]
    [Authorize(Policy = Policies.Buyer)]
    [EnableRateLimiting(RateLimitPolicies.Checkout)]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingDto>> Create([FromBody] CreateBookingRequest request, CancellationToken ct)
    {
        var result = await _sender.Send(new CreateBookingCommand(request.ResourceId, request.StartTime, request.EndTime), ct);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BookingDto>>> List(
        [FromQuery] BookingStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
        => Ok(await _sender.Send(new GetBookingsQuery(status, page, pageSize), ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDto>> GetById(Guid id, CancellationToken ct)
        => Ok(await _sender.Send(new GetBookingByIdQuery(id), ct));

    /// <summary>The escrow saga's current state and the deadline it is waiting on.</summary>
    [HttpGet("{id:guid}/workflow")]
    public async Task<ActionResult<WorkflowStateDto>> GetWorkflow(Guid id, CancellationToken ct)
        => Ok(await _sender.Send(new GetBookingWorkflowQuery(id), ct));

    [HttpGet("available-slots")]
    public async Task<ActionResult<IReadOnlyList<TimeSlotDto>>> GetAvailableSlots(
        [FromQuery] Guid resourceId, [FromQuery] DateOnly targetDate, [FromQuery] int durationMinutes = 60, CancellationToken ct = default)
        => Ok(await _sender.Send(new GetAvailableSlotsQuery(resourceId, targetDate, durationMinutes), ct));

    [HttpPost("{id:guid}/pay")]
    [Authorize(Policy = Policies.Buyer)]
    public async Task<ActionResult<BookingDto>> Pay(Guid id, CancellationToken ct)
        => Ok(await _sender.Send(new PayForBookingCommand(id), ct));

    [HttpPost("{id:guid}/confirm")]
    [Authorize(Policy = Policies.Provider)]
    public async Task<ActionResult<BookingDto>> Confirm(Guid id, CancellationToken ct)
        => Ok(await _sender.Send(new ConfirmBookingCommand(id), ct));

    [HttpPost("{id:guid}/complete")]
    [Authorize(Policy = Policies.Provider)]
    public async Task<ActionResult<BookingDto>> Complete(Guid id, CancellationToken ct)
        => Ok(await _sender.Send(new CompleteBookingCommand(id), ct));

    /// <summary>Buyer approves the session early, releasing the payout before the inspection window ends.</summary>
    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = Policies.Buyer)]
    public async Task<ActionResult<BookingDto>> Approve(Guid id, CancellationToken ct)
        => Ok(await _sender.Send(new ApproveBookingCommand(id), ct));

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<BookingDto>> Cancel(Guid id, [FromBody] CancelBookingRequest request, CancellationToken ct)
        => Ok(await _sender.Send(new CancelBookingCommand(id, request.Reason), ct));

    [HttpPost("{id:guid}/dispute")]
    public async Task<ActionResult<DisputeDto>> Dispute(Guid id, [FromBody] RaiseDisputeRequest request, CancellationToken ct)
        => Ok(await _sender.Send(new RaiseDisputeCommand(id, request.Reason, request.EvidenceSummary ?? string.Empty), ct));
}

public record CreateBookingRequest(Guid ResourceId, DateTimeOffset StartTime, DateTimeOffset EndTime);
public record CancelBookingRequest(string Reason);
public record RaiseDisputeRequest(string Reason, string? EvidenceSummary);
