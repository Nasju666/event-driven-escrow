namespace EscrowMarketplace.Domain.Enums;

// Numeric values are persisted. The GiST exclusion constraint frees a slot for
// Cancelled (5), Expired (6) and Refunded (7), so keep those values stable.
public enum BookingStatus
{
    PendingPayment = 0,   // slot reserved, waiting for the buyer to pay
    Held = 1,             // paid into escrow, waiting for the provider to accept
    Confirmed = 2,        // provider accepted
    InService = 3,        // session in progress (optional step)
    Completed = 4,        // provider marked done, inspection window running
    Cancelled = 5,
    Expired = 6,          // buyer never paid
    Refunded = 7,
    Disputed = 8,
    Settled = 9           // payout released to the provider
}

public enum EscrowStatus
{
    PendingAuthorization = 0,
    Authorized = 1,
    Funded = 2,
    Settled = 3,
    Refunded = 4,
    Disputed = 5,
    Voided = 6            // booking ended before any money moved
}

public enum LedgerEntryType
{
    HoldDeposit = 0,
    ProviderPayout = 1,
    PlatformFee = 2,
    RefundToBuyer = 3,
    DisputeAdjustment = 4
}

public enum DisputeStatus
{
    PendingReview = 0,
    UnderArbitration = 1,
    ResolvedInFavorOfBuyer = 2,
    ResolvedInFavorOfProvider = 3,
    Dismissed = 4
}

public enum UserRole
{
    Buyer = 0,
    Provider = 1,
    Manager = 2,
    Admin = 3
}
