using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Events;
using EscrowMarketplace.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Application.Features.Escrows.Commands;

public record AuthorizeEscrowFundsCommand(Guid EscrowAccountId, string PaymentReference) : IRequest<EscrowDto>;

public class AuthorizeEscrowFundsCommandHandler : IRequestHandler<AuthorizeEscrowFundsCommand, EscrowDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentTenantService _tenantService;
    private readonly IBookingNotifier _notifier;

    public AuthorizeEscrowFundsCommandHandler(
        IMarketplaceDbContext context,
        ICurrentTenantService tenantService,
        IBookingNotifier notifier)
    {
        _context = context;
        _tenantService = tenantService;
        _notifier = notifier;
    }

    public async Task<EscrowDto> Handle(AuthorizeEscrowFundsCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantService.TenantId 
            ?? throw new DomainException("Tenant context required.");

        var escrow = await _context.EscrowAccounts
            .Include(e => e.LedgerEntries)
            .FirstOrDefaultAsync(e => e.Id == request.EscrowAccountId, cancellationToken)
            ?? throw new NotFoundException(nameof(EscrowAccount), request.EscrowAccountId);

        escrow.MarkAuthorized(request.PaymentReference);
        escrow.AddDomainEvent(new EscrowFundsAuthorizedEvent(escrow.Id, escrow.BookingId, tenantId, escrow.TotalAmount, request.PaymentReference));

        await _context.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyEscrowTransitionAsync(tenantId, escrow.Id, escrow.Status.ToString(), escrow.TotalAmount, cancellationToken);

        return MapToDto(escrow);
    }

    internal static EscrowDto MapToDto(EscrowAccount e) => new(
        e.Id,
        e.BookingId,
        e.TenantId,
        e.BuyerId,
        e.ProviderId,
        e.TotalAmount,
        e.PlatformCommissionPercent,
        e.PlatformCommissionAmount,
        e.ProviderPayoutAmount,
        e.Currency,
        e.Status,
        e.PaymentReference,
        e.AuthorizedAt,
        e.FundedAt,
        e.SettledAt,
        e.RefundedAt,
        e.InspectionDeadlineUtc,
        e.RowVersion,
        e.CreatedAt,
        e.LedgerEntries.Select(l => new EscrowLedgerEntryDto(
            l.Id, l.EntryType, l.Amount, l.Currency, l.Description, l.CreatedAt)).ToList()
    );
}

public record FundEscrowCommand(Guid EscrowAccountId, int InspectionWindowDays = 3) : IRequest<EscrowDto>;

public class FundEscrowCommandHandler : IRequestHandler<FundEscrowCommand, EscrowDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentTenantService _tenantService;
    private readonly IBookingNotifier _notifier;

    public FundEscrowCommandHandler(
        IMarketplaceDbContext context,
        ICurrentTenantService tenantService,
        IBookingNotifier notifier)
    {
        _context = context;
        _tenantService = tenantService;
        _notifier = notifier;
    }

    public async Task<EscrowDto> Handle(FundEscrowCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantService.TenantId 
            ?? throw new DomainException("Tenant context required.");

        var escrow = await _context.EscrowAccounts
            .Include(e => e.Booking)
            .Include(e => e.LedgerEntries)
            .FirstOrDefaultAsync(e => e.Id == request.EscrowAccountId, cancellationToken)
            ?? throw new NotFoundException(nameof(EscrowAccount), request.EscrowAccountId);

        var deadline = DateTimeOffset.UtcNow.AddDays(request.InspectionWindowDays);
        escrow.MarkFunded(deadline);

        if (escrow.Booking != null && escrow.Booking.Status == BookingStatus.PendingPayment)
        {
            escrow.Booking.MarkHeld();
        }

        escrow.AddDomainEvent(new EscrowFundsCapturedEvent(escrow.Id, escrow.BookingId, tenantId, escrow.TotalAmount, deadline));

        await _context.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyEscrowTransitionAsync(tenantId, escrow.Id, escrow.Status.ToString(), escrow.TotalAmount, cancellationToken);

        return AuthorizeEscrowFundsCommandHandler.MapToDto(escrow);
    }
}

public record ReleaseEscrowPayoutCommand(Guid EscrowAccountId) : IRequest<EscrowDto>;

public class ReleaseEscrowPayoutCommandHandler : IRequestHandler<ReleaseEscrowPayoutCommand, EscrowDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentTenantService _tenantService;
    private readonly IBookingNotifier _notifier;

    public ReleaseEscrowPayoutCommandHandler(
        IMarketplaceDbContext context,
        ICurrentTenantService tenantService,
        IBookingNotifier notifier)
    {
        _context = context;
        _tenantService = tenantService;
        _notifier = notifier;
    }

    public async Task<EscrowDto> Handle(ReleaseEscrowPayoutCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantService.TenantId 
            ?? throw new DomainException("Tenant context required.");

        var escrow = await _context.EscrowAccounts
            .Include(e => e.Booking)
            .Include(e => e.LedgerEntries)
            .FirstOrDefaultAsync(e => e.Id == request.EscrowAccountId, cancellationToken)
            ?? throw new NotFoundException(nameof(EscrowAccount), request.EscrowAccountId);

        escrow.ReleasePayout();

        if (escrow.Booking != null && escrow.Booking.Status != BookingStatus.Completed)
        {
            escrow.Booking.Complete();
        }

        // Credit provider's wallet balance
        var provider = await _context.Users.FirstOrDefaultAsync(u => u.Id == escrow.ProviderId, cancellationToken);
        if (provider != null)
        {
            provider.WalletBalance += escrow.ProviderPayoutAmount;
        }

        escrow.AddDomainEvent(new EscrowPayoutReleasedEvent(
            escrow.Id,
            escrow.BookingId,
            tenantId,
            escrow.ProviderId,
            escrow.ProviderPayoutAmount,
            escrow.PlatformCommissionAmount));

        await _context.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyEscrowTransitionAsync(tenantId, escrow.Id, escrow.Status.ToString(), escrow.ProviderPayoutAmount, cancellationToken);

        return AuthorizeEscrowFundsCommandHandler.MapToDto(escrow);
    }
}

public record RefundEscrowCommand(Guid EscrowAccountId, string Reason) : IRequest<EscrowDto>;

public class RefundEscrowCommandHandler : IRequestHandler<RefundEscrowCommand, EscrowDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentTenantService _tenantService;
    private readonly IBookingNotifier _notifier;

    public RefundEscrowCommandHandler(
        IMarketplaceDbContext context,
        ICurrentTenantService tenantService,
        IBookingNotifier notifier)
    {
        _context = context;
        _tenantService = tenantService;
        _notifier = notifier;
    }

    public async Task<EscrowDto> Handle(RefundEscrowCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantService.TenantId 
            ?? throw new DomainException("Tenant context required.");

        var escrow = await _context.EscrowAccounts
            .Include(e => e.Booking)
            .Include(e => e.LedgerEntries)
            .FirstOrDefaultAsync(e => e.Id == request.EscrowAccountId, cancellationToken)
            ?? throw new NotFoundException(nameof(EscrowAccount), request.EscrowAccountId);

        escrow.Refund(request.Reason);

        if (escrow.Booking != null)
        {
            escrow.Booking.Refund();
        }

        // Refund buyer's wallet balance
        var buyer = await _context.Users.FirstOrDefaultAsync(u => u.Id == escrow.BuyerId, cancellationToken);
        if (buyer != null)
        {
            buyer.WalletBalance += escrow.TotalAmount;
        }

        await _context.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyEscrowTransitionAsync(tenantId, escrow.Id, escrow.Status.ToString(), escrow.TotalAmount, cancellationToken);

        return AuthorizeEscrowFundsCommandHandler.MapToDto(escrow);
    }
}

public record RaiseDisputeCommand(
    Guid EscrowAccountId,
    Guid RaisedByUserId,
    string Reason,
    string EvidenceSummary
) : IRequest<DisputeDto>;

public class RaiseDisputeCommandHandler : IRequestHandler<RaiseDisputeCommand, DisputeDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentTenantService _tenantService;
    private readonly IBookingNotifier _notifier;

    public RaiseDisputeCommandHandler(
        IMarketplaceDbContext context,
        ICurrentTenantService tenantService,
        IBookingNotifier notifier)
    {
        _context = context;
        _tenantService = tenantService;
        _notifier = notifier;
    }

    public async Task<DisputeDto> Handle(RaiseDisputeCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantService.TenantId 
            ?? throw new DomainException("Tenant context required.");

        var escrow = await _context.EscrowAccounts
            .Include(e => e.Booking)
            .FirstOrDefaultAsync(e => e.Id == request.EscrowAccountId, cancellationToken)
            ?? throw new NotFoundException(nameof(EscrowAccount), request.EscrowAccountId);

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == request.RaisedByUserId, cancellationToken)
            ?? throw new NotFoundException(nameof(User), request.RaisedByUserId);

        escrow.MarkDisputed();
        if (escrow.Booking != null)
        {
            escrow.Booking.MarkDisputed();
        }

        var dispute = new Dispute
        {
            TenantId = tenantId,
            EscrowAccountId = escrow.Id,
            RaisedByUserId = request.RaisedByUserId,
            Reason = request.Reason,
            EvidenceSummary = request.EvidenceSummary,
            Status = DisputeStatus.PendingReview
        };

        _context.Disputes.Add(dispute);
        escrow.AddDomainEvent(new EscrowDisputeRaisedEvent(dispute.Id, escrow.Id, escrow.BookingId, tenantId, request.RaisedByUserId, request.Reason));

        await _context.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyStatusChangedAsync(tenantId, escrow.BookingId, "Disputed", $"Dispute raised: {request.Reason}", cancellationToken);

        return new DisputeDto(
            dispute.Id,
            dispute.EscrowAccountId,
            escrow.BookingId,
            dispute.RaisedByUserId,
            user.FullName,
            dispute.Reason,
            dispute.EvidenceSummary,
            dispute.Status,
            dispute.ResolutionNotes,
            dispute.ResolvedAt,
            dispute.CreatedAt);
    }
}

public record ResolveDisputeCommand(
    Guid DisputeId,
    DisputeStatus Resolution,
    string ResolutionNotes
) : IRequest<DisputeDto>;

public class ResolveDisputeCommandHandler : IRequestHandler<ResolveDisputeCommand, DisputeDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentTenantService _tenantService;
    private readonly IBookingNotifier _notifier;

    public ResolveDisputeCommandHandler(
        IMarketplaceDbContext context,
        ICurrentTenantService tenantService,
        IBookingNotifier notifier)
    {
        _context = context;
        _tenantService = tenantService;
        _notifier = notifier;
    }

    public async Task<DisputeDto> Handle(ResolveDisputeCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantService.TenantId 
            ?? throw new DomainException("Tenant context required.");

        var dispute = await _context.Disputes
            .Include(d => d.RaisedByUser)
            .Include(d => d.EscrowAccount)
            .ThenInclude(e => e!.Booking)
            .FirstOrDefaultAsync(d => d.Id == request.DisputeId, cancellationToken)
            ?? throw new NotFoundException(nameof(Dispute), request.DisputeId);

        dispute.Resolve(request.Resolution, request.ResolutionNotes);

        var escrow = dispute.EscrowAccount;
        if (escrow != null)
        {
            if (request.Resolution == DisputeStatus.ResolvedInFavorOfBuyer)
            {
                escrow.Refund($"Arbitration resolved in favor of buyer: {request.ResolutionNotes}");
                escrow.Booking?.Refund();
            }
            else if (request.Resolution == DisputeStatus.ResolvedInFavorOfProvider)
            {
                escrow.ReleasePayout();
                escrow.Booking?.Complete();
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new DisputeDto(
            dispute.Id,
            dispute.EscrowAccountId,
            escrow?.BookingId ?? Guid.Empty,
            dispute.RaisedByUserId,
            dispute.RaisedByUser?.FullName ?? string.Empty,
            dispute.Reason,
            dispute.EvidenceSummary,
            dispute.Status,
            dispute.ResolutionNotes,
            dispute.ResolvedAt,
            dispute.CreatedAt);
    }
}
