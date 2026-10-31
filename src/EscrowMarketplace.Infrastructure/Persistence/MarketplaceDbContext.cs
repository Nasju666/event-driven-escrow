using System.Linq.Expressions;
using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Domain.Common;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Events;
using EscrowMarketplace.Infrastructure.Messaging;
using EscrowMarketplace.Infrastructure.Saga;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EscrowMarketplace.Infrastructure.Persistence;

public class MarketplaceDbContext : DbContext, IMarketplaceDbContext
{
    private readonly ICurrentTenantService _currentTenantService;
    private readonly IServiceProvider _serviceProvider;

    public MarketplaceDbContext(
        DbContextOptions<MarketplaceDbContext> options,
        ICurrentTenantService currentTenantService,
        IServiceProvider serviceProvider) : base(options)
    {
        _currentTenantService = currentTenantService;
        _serviceProvider = serviceProvider;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<BookableResource> BookableResources => Set<BookableResource>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<EscrowAccount> EscrowAccounts => Set<EscrowAccount>();
    public DbSet<EscrowLedgerEntry> EscrowLedgerEntries => Set<EscrowLedgerEntry>();
    public DbSet<Dispute> Disputes => Set<Dispute>();
    public DbSet<EscrowBookingState> EscrowWorkflows => Set<EscrowBookingState>();

    /// <summary>Read by the global query filters on every query, so it always reflects the current scope.</summary>
    public Guid? CurrentTenantId => _currentTenantService.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // btree_gist lets the exclusion constraint combine "=" on ids with "&&" on time ranges.
        modelBuilder.HasPostgresExtension("btree_gist");

        var isNpgsql = Database.IsNpgsql();

        // MassTransit transactional outbox/inbox tables and the escrow saga's state table.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
        new EscrowBookingStateMap(useXminConcurrency: isNpgsql).Configure(modelBuilder);

        modelBuilder.Entity<Tenant>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasIndex(t => t.Identifier).IsUnique();
            b.Property(t => t.Name).HasMaxLength(150).IsRequired();
            b.Property(t => t.Identifier).HasMaxLength(50).IsRequired();
            b.Property(t => t.Currency).HasMaxLength(10).HasDefaultValue("USD");
            b.Property(t => t.DefaultPlatformCommissionPercent).HasPrecision(5, 2);
        });

        modelBuilder.Entity<User>(b =>
        {
            b.HasKey(u => u.Id);
            b.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();
            b.Property(u => u.Email).HasMaxLength(255).IsRequired();
            b.Property(u => u.FullName).HasMaxLength(150).IsRequired();
            b.Property(u => u.WalletBalance).HasPrecision(18, 2);
            if (isNpgsql) b.Property(u => u.RowVersion).IsRowVersion();
            else b.Ignore(u => u.RowVersion);
        });

        modelBuilder.Entity<BookableResource>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => new { r.TenantId, r.Title });
            b.Property(r => r.Title).HasMaxLength(150).IsRequired();
            b.Property(r => r.Description).HasMaxLength(1000);
            b.Property(r => r.HourlyRate).HasPrecision(18, 2);
            b.Property(r => r.Currency).HasMaxLength(10).HasDefaultValue("USD");

            b.HasOne(r => r.Provider)
             .WithMany()
             .HasForeignKey(r => r.ProviderId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Booking>(b =>
        {
            b.HasKey(e => e.Id);
            b.HasIndex(e => new { e.TenantId, e.ResourceId, e.Status });
            b.HasIndex(e => new { e.TenantId, e.BuyerId });
            b.HasIndex(e => new { e.TenantId, e.ProviderId });

            // Column names are referenced by the GiST exclusion constraint. Keep them in sync.
            b.OwnsOne(e => e.Period, p =>
            {
                p.Property(pr => pr.Start).HasColumnName("period_start").IsRequired();
                p.Property(pr => pr.End).HasColumnName("period_end").IsRequired();
            });

            b.Property(e => e.TotalAmount).HasPrecision(18, 2);
            b.Property(e => e.Currency).HasMaxLength(10).HasDefaultValue("USD");
            b.Property(e => e.CancellationReason).HasMaxLength(500);

            if (isNpgsql) b.Property(e => e.RowVersion).IsRowVersion();
            else b.Ignore(e => e.RowVersion);

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
            b.Property(e => e.PaymentReference).HasMaxLength(100);

            if (isNpgsql) b.Property(e => e.RowVersion).IsRowVersion();
            else b.Ignore(e => e.RowVersion);

            b.HasOne(e => e.Booking)
             .WithOne(bk => bk.EscrowAccount)
             .HasForeignKey<EscrowAccount>(e => e.BookingId)
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

        modelBuilder.Entity<EscrowLedgerEntry>(b =>
        {
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.TenantId, l.EscrowAccountId });
            b.Property(l => l.Amount).HasPrecision(18, 2);
            b.Property(l => l.Currency).HasMaxLength(10).HasDefaultValue("USD");
            b.Property(l => l.Description).HasMaxLength(1000);

            b.HasOne(l => l.EscrowAccount)
             .WithMany(e => e.LedgerEntries)
             .HasForeignKey(l => l.EscrowAccountId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Dispute>(b =>
        {
            b.HasKey(d => d.Id);
            b.HasIndex(d => new { d.TenantId, d.EscrowAccountId });
            b.HasIndex(d => new { d.TenantId, d.BookingId });
            b.Property(d => d.Reason).HasMaxLength(500).IsRequired();
            b.Property(d => d.EvidenceSummary).HasMaxLength(2000);
            b.Property(d => d.ResolutionNotes).HasMaxLength(2000);
            b.Ignore(d => d.IsOpen);

            b.HasOne(d => d.EscrowAccount)
             .WithMany(e => e.Disputes)
             .HasForeignKey(d => d.EscrowAccountId)
             .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(d => d.RaisedByUser)
             .WithMany()
             .HasForeignKey(d => d.RaisedByUserId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // Multi-tenant global query filter on every ITenantEntity. It fails closed: when no tenant
        // is set, the filter compares against NULL and matches nothing. Code that legitimately
        // spans tenants (login, background jobs) must opt out explicitly with IgnoreQueryFilters().
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType) || entityType.IsOwned())
                continue;

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var tenantProperty = Expression.Convert(
                Expression.Property(parameter, nameof(ITenantEntity.TenantId)), typeof(Guid?));
            var currentTenantId = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));

            // e => (Guid?)e.TenantId == this.CurrentTenantId
            var filter = Expression.Lambda(Expression.Equal(tenantProperty, currentTenantId), parameter);
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(filter);
        }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAuditAndTenant();

        var domainEvents = CollectDomainEvents();
        var dispatcher = domainEvents.Count > 0 ? _serviceProvider.GetService<IDomainEventDispatcher>() : null;

        if (dispatcher is null)
            return await base.SaveChangesAsync(cancellationToken);

        if (dispatcher.UsesTransactionalOutbox)
        {
            // Publishing through the EF bus outbox only adds OutboxMessage rows to this context.
            // They are committed by the SaveChanges below, in the same transaction as the state
            // change. Either both happen or neither does: no lost or phantom events.
            await dispatcher.DispatchAsync(domainEvents, cancellationToken);
            return await base.SaveChangesAsync(cancellationToken);
        }

        // In-memory demo mode has no outbox, so publish only after the data is saved.
        var result = await base.SaveChangesAsync(cancellationToken);
        await dispatcher.DispatchAsync(domainEvents, cancellationToken);
        return result;
    }

    private List<IDomainEvent> CollectDomainEvents()
    {
        var entities = ChangeTracker.Entries<BaseEntity>()
            .Select(e => e.Entity)
            .Where(e => e.DomainEvents.Count > 0)
            .ToList();

        var events = entities.SelectMany(e => e.DomainEvents).ToList();
        entities.ForEach(e => e.ClearDomainEvents());
        return events;
    }

    private void StampAuditAndTenant()
    {
        var tenantId = _currentTenantService.TenantId;
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Added && entry.Entity.Id == Guid.Empty)
                entry.Entity.Id = Guid.NewGuid();
        }

        foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
        {
            if (entry.State == EntityState.Added && tenantId.HasValue && entry.Entity.TenantId == Guid.Empty)
                entry.Entity.TenantId = tenantId.Value;
        }

        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
                entry.Entity.CreatedAt = now;
            else if (entry.State == EntityState.Modified)
                entry.Entity.UpdatedAt = now;
        }
    }
}
