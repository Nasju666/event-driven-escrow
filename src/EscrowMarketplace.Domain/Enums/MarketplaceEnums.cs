namespace EscrowMarketplace.Domain.Enums;

public enum BookingStatus
{
    PendingPayment = 0,
    Held = 1,
    Confirmed = 2,
    InService = 3,
    Completed = 4,
    Cancelled = 5,
    Expired = 6,
    Refunded = 7,
    Disputed = 8
}

public enum EscrowStatus
{
    PendingAuthorization = 0,
    Authorized = 1,
    Funded = 2,
    Settled = 3,
    Refunded = 4,
    Disputed = 5
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
