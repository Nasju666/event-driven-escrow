using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EscrowMarketplace.Infrastructure.Persistence;

public class MarketplaceDbContextInitializer
{
    // Demo identities. Two tenants so tenant isolation can be shown and tested.
    public static readonly Guid ApexTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid ApexAdminId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ApexProviderId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid ApexBuyerId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid ApexSecondBuyerId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    public static readonly Guid LiveRoomId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid MixingSuiteId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    public static readonly Guid VocalBoothId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    public static readonly Guid NorthwindTenantId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    public static readonly Guid NorthwindProviderId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    public static readonly Guid NorthwindBuyerId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid RehearsalRoomId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly MarketplaceDbContext _context;
    private readonly ILogger<MarketplaceDbContextInitializer> _logger;

    public MarketplaceDbContextInitializer(
        MarketplaceDbContext context,
        ILogger<MarketplaceDbContextInitializer> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        if (_context.Database.IsRelational())
        {
            if (_context.Database.GetMigrations().Any())
            {
                await _context.Database.MigrateAsync();
            }
            else
            {
                _logger.LogWarning(
                    "No EF Core migrations found, so the schema is being created with EnsureCreated. " +
                    "Generate migrations with: dotnet ef migrations add InitialCreate " +
                    "--project src/EscrowMarketplace.Infrastructure --startup-project src/EscrowMarketplace.Api");
                await _context.Database.EnsureCreatedAsync();
            }

            await EnsureBookingOverlapConstraintAsync();
        }
        else
        {
            await _context.Database.EnsureCreatedAsync();
        }

        await SeedSampleDataAsync();
    }

    /// <summary>
    /// The real double-booking guarantee. EF Core can't model exclusion constraints, so it is
    /// applied here, idempotently, after migrations. Two bookings for the same resource whose
    /// [start, end) ranges overlap cannot both exist unless one is Cancelled (5), Expired (6) or
    /// Refunded (7). Concurrent inserts are serialized by the GiST index, and the loser gets SQLSTATE 23P01.
    /// Failures are NOT swallowed: without this constraint the app must not start.
    /// </summary>
    private async Task EnsureBookingOverlapConstraintAsync()
    {
        await _context.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS btree_gist;");

        const string sql = @"
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'excl_bookings_no_overlap') THEN
                    ALTER TABLE ""Bookings""
                    ADD CONSTRAINT excl_bookings_no_overlap
                    EXCLUDE USING gist (
                        ""TenantId"" WITH =,
                        ""ResourceId"" WITH =,
                        tstzrange(period_start, period_end, '[)') WITH &&
                    )
                    WHERE (""Status"" NOT IN (5, 6, 7));
                END IF;
            END $$;";

        await _context.Database.ExecuteSqlRawAsync(sql);
        _logger.LogInformation("GiST exclusion constraint excl_bookings_no_overlap is in place.");
    }

    public async Task SeedSampleDataAsync()
    {
        if (await _context.Tenants.AnyAsync()) return;

        // ── Tenant 1: Apex Creative Studios ──
        var apex = new Tenant
        {
            Id = ApexTenantId,
            Name = "Apex Creative Studios",
            Identifier = "apex-studios",
            Currency = "USD",
            DefaultPlatformCommissionPercent = 10.0m,
            IsActive = true,
            TimeZone = "UTC"
        };

        var admin = NewUser(ApexAdminId, apex.Id, "admin@marketplace.local", "Avery Santos", UserRole.Admin, 0m);
        var provider = NewUser(ApexProviderId, apex.Id, "sophia@vancestudios.com", "Sophia Vance", UserRole.Provider, 0m);
        var buyer = NewUser(ApexBuyerId, apex.Id, "marcus@soundwave.io", "Marcus Chen", UserRole.Buyer, 1500.00m);
        var buyer2 = NewUser(ApexSecondBuyerId, apex.Id, "priya@soundwave.io", "Priya Nair", UserRole.Buyer, 800.00m);

        var liveRoom = NewResource(LiveRoomId, apex.Id, provider.Id, "Live Room A",
            "Large live room with a Yamaha C7 grand piano, drum riser and isolation booths.", 75.00m);
        var mixingSuite = NewResource(MixingSuiteId, apex.Id, provider.Id, "Dolby Atmos Mixing Suite",
            "7.1.4 immersive mixing and mastering suite with Genelec monitors and an Avid S6 console.", 120.00m);
        var vocalBooth = NewResource(VocalBoothId, apex.Id, provider.Id, "Vocal Booth B",
            "Treated vocal booth with a Neumann U87, tube preamps and a headphone cue mix.", 45.00m);

        // ── Tenant 2: Northwind Rehearsal Rooms (exists to prove tenant isolation) ──
        var northwind = new Tenant
        {
            Id = NorthwindTenantId,
            Name = "Northwind Rehearsal Rooms",
            Identifier = "northwind",
            Currency = "USD",
            DefaultPlatformCommissionPercent = 8.0m,
            IsActive = true,
            TimeZone = "UTC"
        };

        var nwProvider = NewUser(NorthwindProviderId, northwind.Id, "diego@northwind.test", "Diego Ramos", UserRole.Provider, 0m);
        var nwBuyer = NewUser(NorthwindBuyerId, northwind.Id, "lena@northwind.test", "Lena Fischer", UserRole.Buyer, 600.00m);
        var rehearsalRoom = NewResource(RehearsalRoomId, northwind.Id, nwProvider.Id, "Rehearsal Room 1",
            "Backline-equipped rehearsal room for full bands.", 30.00m);

        _context.Tenants.AddRange(apex, northwind);
        _context.Users.AddRange(admin, provider, buyer, buyer2, nwProvider, nwBuyer);
        _context.BookableResources.AddRange(liveRoom, mixingSuite, vocalBooth, rehearsalRoom);

        await _context.SaveChangesAsync();
        _logger.LogInformation("Seeded demo data: 2 tenants, 6 users, 4 resources.");
    }

    private static User NewUser(Guid id, Guid tenantId, string email, string name, UserRole role, decimal wallet) => new()
    {
        Id = id,
        TenantId = tenantId,
        Email = email,
        FullName = name,
        Role = role,
        IsActive = true,
        WalletBalance = wallet
    };

    private static BookableResource NewResource(Guid id, Guid tenantId, Guid providerId, string title, string description, decimal rate) => new()
    {
        Id = id,
        TenantId = tenantId,
        ProviderId = providerId,
        Title = title,
        Description = description,
        HourlyRate = rate,
        Currency = "USD",
        TimeZone = "UTC",
        IsActive = true
    };
}
