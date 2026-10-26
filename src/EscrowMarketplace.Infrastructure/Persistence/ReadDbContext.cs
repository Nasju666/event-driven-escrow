using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Infrastructure.Persistence;

public class ReadDbContext : IReadDbContext
{
    private readonly MarketplaceDbContext _context;

    public ReadDbContext(MarketplaceDbContext context)
    {
        _context = context;
    }

    public IQueryable<Tenant> Tenants => _context.Tenants.AsNoTracking();
    public IQueryable<User> Users => _context.Users.AsNoTracking();
    public IQueryable<BookableResource> BookableResources => _context.BookableResources.AsNoTracking();
    public IQueryable<Booking> Bookings => _context.Bookings.AsNoTracking();
    public IQueryable<EscrowAccount> EscrowAccounts => _context.EscrowAccounts.AsNoTracking();
    public IQueryable<EscrowLedgerEntry> EscrowLedgerEntries => _context.EscrowLedgerEntries.AsNoTracking();
    public IQueryable<Dispute> Disputes => _context.Disputes.AsNoTracking();
}
