using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Common.Security;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Application.Features.Resources.Commands;

/// <summary>A provider lists a new bookable resource. The provider is the authenticated user.</summary>
public record CreateResourceCommand(
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
        RuleFor(x => x.Title).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.HourlyRate).GreaterThan(0).LessThanOrEqualTo(100_000);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
    }
}

public class CreateResourceCommandHandler : IRequestHandler<CreateResourceCommand, ResourceDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentTenantService _tenant;
    private readonly ICurrentActor _actor;

    public CreateResourceCommandHandler(IMarketplaceDbContext context, ICurrentTenantService tenant, ICurrentActor actor)
    {
        _context = context;
        _tenant = tenant;
        _actor = actor;
    }

    public async Task<ResourceDto> Handle(CreateResourceCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenant.RequireTenantId();
        var providerId = _actor.RequireRole(UserRole.Provider);

        var provider = await _context.Users.FirstAsync(u => u.Id == providerId, cancellationToken);

        var resource = new BookableResource
        {
            TenantId = tenantId,
            ProviderId = provider.Id,
            Title = request.Title,
            Description = request.Description ?? string.Empty,
            HourlyRate = request.HourlyRate,
            Currency = request.Currency.ToUpperInvariant(),
            TimeZone = request.TimeZone,
            IsActive = true
        };

        _context.BookableResources.Add(resource);
        await _context.SaveChangesAsync(cancellationToken);

        return new ResourceDto(
            resource.Id, resource.TenantId, resource.ProviderId, provider.FullName,
            resource.Title, resource.Description, resource.HourlyRate,
            resource.Currency, resource.TimeZone, resource.IsActive);
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
        var query = _readContext.BookableResources.AsQueryable();

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
                r.IsActive))
            .ToListAsync(cancellationToken);
    }
}
