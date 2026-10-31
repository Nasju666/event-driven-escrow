using EscrowMarketplace.Domain.Enums;

namespace EscrowMarketplace.Application.Common.Models;

/// <summary>
/// A booking together with the escrow summary the UI needs, so clients never have to
/// hardcode fees or make a second call to know what happens next.
/// </summary>
public record BookingDto(
    Guid Id,
    Guid TenantId,
    Guid ResourceId,
    string ResourceTitle,
    Guid BuyerId,
    string BuyerName,
    Guid ProviderId,
    string ProviderName,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    decimal TotalAmount,
    string Currency,
    BookingStatus Status,
    DateTimeOffset PaymentDueUtc,
    DateTimeOffset? ProviderResponseDueUtc,
    string? CancellationReason,
    DateTimeOffset CreatedAt,
    Guid? EscrowId,
    EscrowStatus? EscrowStatus,
    decimal PlatformCommissionPercent,
    decimal PlatformCommissionAmount,
    decimal ProviderPayoutAmount,
    DateTimeOffset? InspectionDeadlineUtc,
    Guid? OpenDisputeId
);

public record EscrowDto(
    Guid Id,
    Guid BookingId,
    Guid TenantId,
    Guid BuyerId,
    Guid ProviderId,
    decimal TotalAmount,
    decimal PlatformCommissionPercent,
    decimal PlatformCommissionAmount,
    decimal ProviderPayoutAmount,
    string Currency,
    EscrowStatus Status,
    string? PaymentReference,
    DateTimeOffset? AuthorizedAt,
    DateTimeOffset? FundedAt,
    DateTimeOffset? SettledAt,
    DateTimeOffset? RefundedAt,
    DateTimeOffset? InspectionDeadlineUtc,
    DateTimeOffset CreatedAt,
    IReadOnlyList<EscrowLedgerEntryDto> LedgerEntries
);

public record EscrowLedgerEntryDto(
    Guid Id,
    LedgerEntryType EntryType,
    decimal Amount,
    string Currency,
    string Description,
    DateTimeOffset CreatedAt
);

public record ResourceDto(
    Guid Id,
    Guid TenantId,
    Guid ProviderId,
    string ProviderName,
    string Title,
    string Description,
    decimal HourlyRate,
    string Currency,
    string TimeZone,
    bool IsActive
);

public record TimeSlotDto(
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    bool IsAvailable
);

public record DisputeDto(
    Guid Id,
    Guid EscrowAccountId,
    Guid BookingId,
    Guid RaisedByUserId,
    string RaisedByUserName,
    string Reason,
    string EvidenceSummary,
    DisputeStatus Status,
    string? ResolutionNotes,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset CreatedAt
);

public record CurrentUserDto(
    Guid Id,
    string FullName,
    string Email,
    UserRole Role,
    Guid TenantId,
    string TenantName,
    decimal WalletBalance,
    string Currency
);
