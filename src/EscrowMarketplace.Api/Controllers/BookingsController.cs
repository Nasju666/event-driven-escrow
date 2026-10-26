using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Features.Bookings.Commands;
using EscrowMarketplace.Application.Features.Bookings.Queries;
using EscrowMarketplace.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace EscrowMarketplace.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BookingsController : ControllerBase
{
    private readonly ISender _sender;

    public BookingsController(ISender sender)
    {
        _sender = sender;
    }

    [HttpPost]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingDto>> CreateBooking(
        [FromBody] CreateBookingCommand command,
        CancellationToken cancellationToken)
    {
        // Auto-populate buyerId from authenticated user claim if not explicitly provided
        if (command.BuyerId == Guid.Empty && User.Identity?.IsAuthenticated == true)
        {
            var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("sub")?.Value;

            if (Guid.TryParse(userIdClaim, out var buyerGuid))
            {
                command = command with { BuyerId = buyerGuid };
            }
        }

        var result = await _sender.Send(command, cancellationToken);
        return CreatedAtAction(nameof(GetBookingById), new { id = result.Id }, result);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDto>> GetBookingById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetBookingByIdQuery(id), cancellationToken);
        return Ok(result);
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<BookingDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BookingDto>>> GetBookings(
        [FromQuery] BookingStatus? status,
        [FromQuery] Guid? buyerId,
        [FromQuery] Guid? providerId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _sender.Send(new GetBookingsByTenantQuery(status, buyerId, providerId, page, pageSize), cancellationToken);
        return Ok(result);
    }

    [HttpGet("available-slots")]
    [ProducesResponseType(typeof(IReadOnlyList<TimeSlotDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TimeSlotDto>>> GetAvailableSlots(
        [FromQuery] Guid resourceId,
        [FromQuery] DateOnly targetDate,
        [FromQuery] int durationMinutes = 60,
        CancellationToken cancellationToken = default)
    {
        var result = await _sender.Send(new GetAvailableSlotsQuery(resourceId, targetDate, durationMinutes), cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/confirm")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BookingDto>> ConfirmBooking(
        Guid id,
        [FromBody] ConfirmBookingRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new ConfirmBookingCommand(id, request.ProviderId), cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BookingDto>> CompleteBooking(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new CompleteBookingCommand(id), cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BookingDto>> CancelBooking(
        Guid id,
        [FromBody] CancelBookingRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new CancelBookingCommand(id, request.Reason), cancellationToken);
        return Ok(result);
    }
}

public record ConfirmBookingRequest(Guid ProviderId);
public record CancelBookingRequest(string Reason);
