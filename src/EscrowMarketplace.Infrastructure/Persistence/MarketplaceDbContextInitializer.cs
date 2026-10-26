using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EscrowMarketplace.Infrastructure.Persistence;

public class MarketplaceDbContextInitializer
{
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
        try
        {
            if (_context.Database.IsRelational())
            {
                await _context.Database.EnsureCreatedAsync();

                // Ensure btree_gist extension and GiST exclusion constraint on PostgreSQL
                try
                {
                    await _context.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS btree_gist;");
                    
                    // Add GiST exclusion constraint if not already present
                    var sqlConstraint = @"
                        DO $$
                        BEGIN
                            IF NOT EXISTS (
                                SELECT 1 FROM pg_constraint WHERE conname = 'excl_bookings_no_overlap'
                            ) THEN
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

                    await _context.Database.ExecuteSqlRawAsync(sqlConstraint);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Note: GiST exclusion constraint script was skipped or already exists.");
                }
            }
            else
            {
                await _context.Database.EnsureCreatedAsync();
            }

            await SeedSampleDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while initializing the database.");
            throw;
        }
    }

    public async Task SeedSampleDataAsync()
    {
        if (await _context.Tenants.AnyAsync()) return;

        // 1. Seed Tenant
        var tenant = new Tenant
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Apex Creative Spaces & Studios",
            Identifier = "apex-studios",
            Currency = "USD",
            DefaultPlatformCommissionPercent = 10.0m,
            IsActive = true,
            TimeZone = "UTC"
        };
        _context.Tenants.Add(tenant);

        // 2. Seed Users
        var admin = new User
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            TenantId = tenant.Id,
            Email = "admin@marketplace.local",
            FullName = "Platform Administrator",
            Role = UserRole.Admin,
            IsActive = true,
            WalletBalance = 0m
        };

        var provider = new User
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            TenantId = tenant.Id,
            Email = "sophia@vancestudios.com",
            FullName = "Sophia Vance (Studio Host)",
            Role = UserRole.Provider,
            IsActive = true,
            WalletBalance = 250.00m
        };

        var buyer = new User
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            TenantId = tenant.Id,
            Email = "marcus@soundwave.io",
            FullName = "Marcus Chen (Audio Producer)",
            Role = UserRole.Buyer,
            IsActive = true,
            WalletBalance = 1500.00m
        };

        _context.Users.AddRange(admin, provider, buyer);

        // 3. Seed Bookable Resources
        var resource1 = new BookableResource
        {
            Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            TenantId = tenant.Id,
            ProviderId = provider.Id,
            Title = "Master Acoustic Live Room A",
            Description = "State-of-the-art live recording room with 14ft diffusers, Yamaha C7 grand piano, and multi-mic routing.",
            HourlyRate = 75.00m,
            Currency = "USD",
            TimeZone = "UTC",
            IsActive = true
        };

        var resource2 = new BookableResource
        {
            Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
            TenantId = tenant.Id,
            ProviderId = provider.Id,
            Title = "Dolby Atmos Mixing & Mastering Suite",
            Description = "7.1.4 immersive audio mastering suite equipped with Genelec SAM monitors and Avid S6 console.",
            HourlyRate = 120.00m,
            Currency = "USD",
            TimeZone = "UTC",
            IsActive = true
        };

        var resource3 = new BookableResource
        {
            Id = Guid.Parse("77777777-7777-7777-7777-777777777777"),
            TenantId = tenant.Id,
            ProviderId = provider.Id,
            Title = "Vocal Isolation Booth & Neumann U87",
            Description = "Acoustically treated dry vocal booth with tube preamps and headphone cue mixers.",
            HourlyRate = 45.00m,
            Currency = "USD",
            TimeZone = "UTC",
            IsActive = true
        };

        _context.BookableResources.AddRange(resource1, resource2, resource3);

        await _context.SaveChangesAsync();
        _logger.LogInformation("Database seeded successfully with sample tenant, users, and studio resources.");
    }
}
