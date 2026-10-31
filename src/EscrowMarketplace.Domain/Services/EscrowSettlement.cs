using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Exceptions;

namespace EscrowMarketplace.Domain.Services;

/// <summary>
/// The only two ways money leaves escrow. Every path that settles a booking (buyer approval,
/// automatic release after the inspection window, dispute resolution, cancellation) goes
/// through here, so wallets, ledger and statuses always move together.
/// </summary>
public static class EscrowSettlement
{
    public static void ReleaseToProvider(Booking booking, EscrowAccount escrow, User provider)
    {
        EnsureBelongsTogether(booking, escrow);
        if (provider.Id != escrow.ProviderId)
            throw new DomainException("Payout recipient does not match the escrow provider.");

        escrow.ReleasePayout();
        provider.Credit(escrow.ProviderPayoutAmount);
        booking.MarkSettled();
    }

    public static void RefundToBuyer(Booking booking, EscrowAccount escrow, User buyer, string reason)
    {
        EnsureBelongsTogether(booking, escrow);
        if (buyer.Id != escrow.BuyerId)
            throw new DomainException("Refund recipient does not match the escrow buyer.");

        escrow.Refund(reason);
        buyer.Credit(escrow.TotalAmount);
        booking.MarkRefunded();
    }

    private static void EnsureBelongsTogether(Booking booking, EscrowAccount escrow)
    {
        if (escrow.BookingId != booking.Id)
            throw new DomainException("Escrow account does not belong to this booking.");
    }
}
