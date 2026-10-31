namespace EscrowMarketplace.Application.Common.Settings;

/// <summary>
/// Time windows for the escrow workflow. Handlers stamp the deadlines onto the aggregates and
/// the saga schedules a timeout message for each one. Bound from the "EscrowWorkflow" section;
/// appsettings.Development.json shortens them so the whole flow can be demoed in minutes.
/// </summary>
public class EscrowWorkflowOptions
{
    public const string SectionName = "EscrowWorkflow";

    /// <summary>How long a reserved slot is held before an unpaid booking expires.</summary>
    public TimeSpan PaymentWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long the provider has to accept a paid booking before it is cancelled and refunded.</summary>
    public TimeSpan ProviderResponseWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long the buyer has to dispute after completion before the payout is released automatically.</summary>
    public TimeSpan InspectionWindow { get; set; } = TimeSpan.FromHours(72);
}
