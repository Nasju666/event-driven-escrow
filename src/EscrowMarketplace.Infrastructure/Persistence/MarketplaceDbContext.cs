using System.Linq.Expressions;
using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Domain.Common;
using EscrowMarketplace.Domain.Entities;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Infrastructure.Persistence;

public class MarketplaceDbContext : DbContext, IMarketplaceDbContext
{
    private readonly ICurrentTenantService _currentTenantService;

    public MarketplaceDbContext(
        DbContextOptions<MarketplaceDbContext> options,
        ICurrentTenantService currentTenantService) : base(options)
    {
        _currentTenantService = currentTenantService;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<BookableResource> BookableResources => Set<BookableResource>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<EscrowAccount> EscrowAccounts => Set<EscrowAccount>();
    public DbSet<EscrowLedgerEntry> EscrowLedgerEntries => Set<EscrowLedgerEntry>();
    public DbSet<Dispute> Disputes => Set<Dispute>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // PostgreSQL GiST extension for range overlap exclusion constraints
        modelBuilder.HasPostgresExtension("btree_gist");

        // MassTransit Transactional Outbox & Inbox Entities
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        // 1. Tenant Entity
        modelBuilder.Entity<Tenant>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasIndex(t => t.Identifier).IsUnique();
            b.Property(t => t.Name).HasMaxLength(150).IsRequired();
            b.Property(t => t.Identifier).HasMaxLength(50).IsRequired();
            b.Property(t => t.Currency).HasMaxLength(10).HasDefaultValue("USD");
            b.Property(t => t.DefaultPlatformCommissionPercent).HasPrecision(5, 2);
        });

        // 2. User Entity
        modelBuilder.Entity<User>(b =>
        {
            b.HasKey(u => u.Id);
            b.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();
            b.Property(u => u.Email).HasMaxLength(255).IsRequired();
            b.Property(u => u.FullName).HasMaxLength(150).IsRequired();
            b.Property(u => u.WalletBalance).HasPrecision(18, 2);
        });

        // 3. BookableResource Entity
        modelBuilder.Entity<BookableResource>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => new { r.TenantId, r.Title });
            b.Property(r => r.Title).HasMaxLength(150).IsRequired();
            b.Property(r => r.HourlyRate).HasPrecision(18, 2);
            b.Property(r => r.Currency).HasMaxLength(10).HasDefaultValue("USD");

            b.HasOne(r => r.Provider)
             .WithMany()
             .HasForeignKey(r => r.ProviderId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // 4. Booking Entity with TimeRange Value Object & Concurrency Token
        modelBuilder.Entity<Booking>(b =>
        {
            b.HasKey(e => e.Id);
            b.HasIndex(e => new { e.TenantId, e.ResourceId, e.Status });

            b.OwnsOne(e => e.Period, p =>
            {
                p.Property(pr => pr.Start).HasColumnName("period_start").IsRequired();
                p.Property(pr => pr.End).HasColumnName("period_end").IsRequired();
            });

            b.Property(e => e.TotalAmount).HasPrecision(18, 2);
            b.Property(e => e.Currency).HasMaxLength(10).HasDefaultValue("USD");

            // PostgreSQL xmin optimistic concurrency token (configured for relational providers)
            if (Database.IsNpgsql())
            {
                b.Property(e => e.RowVersion).IsRowVersion();
            }

            b.HasOne(e => e.Resource)
             .WithMany(r => r.Bookings)
             .HasForeignKey(e => e.ResourceId)
             .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(e => e.Buyer)
             .WithMany()
             .HasForeignKey(e => e.BuyerId)
             .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(e => e.Provider)
             .WithMany()
             .HasForeignKey(e => e.ProviderId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // 5. EscrowAccount Entity
        modelBuilder.Entity<EscrowAccount>(b =>
        {
            b.HasKey(e => e.Id);
            b.HasIndex(e => e.BookingId).IsUnique();
            b.HasIndex(e => new { e.TenantId, e.Status });

            b.Property(e => e.TotalAmount).HasPrecision(18, 2);
            b.Property(e => e.PlatformCommissionPercent).HasPrecision(5, 2);
            b.Property(e => e.PlatformCommissionAmount).HasPrecision(18, 2);
            b.Property(e => e.ProviderPayoutAmount).HasPrecision(18, 2);
            b.Property(e => e.Currency).HasMaxLength(10).HasDefaultValue("USD");

            // PostgreSQL xmin optimistic concurrency token (configured for relational providers)
            if (Database.IsNpgsql())
            {
                b.Property(e => e.RowVersion).IsRowVersion();
            }

            b.HasOne(e => e.Booking)
             .WithOne(b => b.EscrowAccount)
             .HasForeignKey<EscrowAccount>(e => e.BookingId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // 6. EscrowLedgerEntry Entity
        modelBuilder.Entity<EscrowLedgerEntry>(b =>
        {
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.TenantId, l.EscrowAccountId });
            b.Property(l => l.Amount).HasPrecision(18, 2);
            b.Property(l => l.Currency).HasMaxLength(10).HasDefaultValue("USD");

            b.HasOne(l => l.EscrowAccount)
             .WithMany(e => e.LedgerEntries)
             .HasForeignKey(l => l.EscrowAccountId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // 7. Dispute Entity
        modelBuilder.Entity<Dispute>(b =>
        {
            b.HasKey(d => d.Id);
            b.HasIndex(d => new { d.TenantId, d.EscrowAccountId });

            b.HasOne(d => d.EscrowAccount)
             .WithMany(e => e.Disputes)
             .HasForeignKey(d => d.EscrowAccountId)
             .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(d => d.RaisedByUser)
             .WithMany()
             .HasForeignKey(d => d.RaisedByUserId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // Multi-tenant Global Query Filters dynamically applied to all ITenantEntity implementations
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType))
            {
                var parameter = Expression.Parameter(entityType.ClrType, "e");
                var tenantProperty = Expression.Property(parameter, nameof(ITenantEntity.TenantId));
                var currentTenantId = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));

                // e => CurrentTenantId == null || e.TenantId == CurrentTenantId
                var filter = Expression.Lambda(
                    Expression.OrElse(
                        Expression.Equal(currentTenantId, Expression.Constant(null, typeof(Guid?))),
                        Expression.Equal(tenantProperty, Expression.Convert(currentTenantId, typeof(Guid)))),
                    parameter);

                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(filter);
            }
        }
    }

    public Guid? CurrentTenantId => _currentTenantService.TenantId;

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _currentTenantService.TenantId;

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Added && entry.Entity.Id == Guid.Empty)
            {
                entry.Entity.Id = Guid.NewGuid();
            }
        }

        foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
        {
            if (entry.State == EntityState.Added && tenantId.HasValue && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = tenantId.Value;
            }
        }

        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = DateTimeOffset.UtcNow;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        return await base.SaveChangesAsync(cancellationToken);
    }
}
