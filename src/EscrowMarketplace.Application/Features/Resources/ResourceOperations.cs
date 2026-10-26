using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Application.Features.Resources.Commands;

public record CreateResourceCommand(
    Guid ProviderId,
    string Title,
    string Description,
    decimal HourlyRate,
    string Currency = "USD",
    string TimeZone = "UTC"
) : IRequest<ResourceDto>;

public class CreateResourceCommandValidator : AbstractValidator<CreateResourceCommand>
{
    public CreateResourceCommandValidator()
    {
        RuleFor(x => x.ProviderId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(150);
        RuleFor(x => x.HourlyRate).GreaterThan(0);
    }
}

public class CreateResourceCommandHandler : IRequestHandler<CreateResourceCommand, ResourceDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentTenantService _tenantService;

    public CreateResourceCommandHandler(IMarketplaceDbContext context, ICurrentTenantService tenantService)
    {
        _context = context;
        _tenantService = tenantService;
    }

    public async Task<ResourceDto> Handle(CreateResourceCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantService.TenantId 
            ?? throw new DomainException("Tenant context is required.");

        var provider = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == request.ProviderId && u.IsActive, cancellationToken)
            ?? throw new NotFoundException(nameof(User), request.ProviderId);

        var resource = new BookableResource
        {
            TenantId = tenantId,
            ProviderId = provider.Id,
            Title = request.Title,
            Description = request.Description,
            HourlyRate = request.HourlyRate,
            Currency = request.Currency,
            TimeZone = request.TimeZone,
            IsActive = true
        };

        _context.BookableResources.Add(resource);
        await _context.SaveChangesAsync(cancellationToken);

        return new ResourceDto(
            resource.Id,
            resource.TenantId,
            resource.ProviderId,
            provider.FullName,
            resource.Title,
            resource.Description,
            resource.HourlyRate,
            resource.Currency,
            resource.TimeZone,
            resource.IsActive);
    }
}

public record GetResourcesQuery(bool OnlyActive = true) : IRequest<IReadOnlyList<ResourceDto>>;

public class GetResourcesQueryHandler : IRequestHandler<GetResourcesQuery, IReadOnlyList<ResourceDto>>
{
    private readonly IReadDbContext _readContext;

    public GetResourcesQueryHandler(IReadDbContext readContext)
    {
        _readContext = readContext;
    }

    public async Task<IReadOnlyList<ResourceDto>> Handle(GetResourcesQuery request, CancellationToken cancellationToken)
    {
        var query = _readContext.BookableResources
            .AsNoTracking()
            .Include(r => r.Provider)
            .AsQueryable();

        if (request.OnlyActive)
        {
            query = query.Where(r => r.IsActive);
        }

        return await query
            .OrderBy(r => r.Title)
            .Select(r => new ResourceDto(
                r.Id,
                r.TenantId,
                r.ProviderId,
                r.Provider != null ? r.Provider.FullName : string.Empty,
                r.Title,
                r.Description,
                r.HourlyRate,
                r.Currency,
                r.TimeZone,
                r.IsActive
            ))
            .ToListAsync(cancellationToken);
    }
}
