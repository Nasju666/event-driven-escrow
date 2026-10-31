using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Common.Security;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Application.Features.Users;

public record GetCurrentUserQuery : IRequest<CurrentUserDto>;

public class GetCurrentUserQueryHandler : IRequestHandler<GetCurrentUserQuery, CurrentUserDto>
{
    private readonly IReadDbContext _readContext;
    private readonly ICurrentActor _actor;

    public GetCurrentUserQueryHandler(IReadDbContext readContext, ICurrentActor actor)
    {
        _readContext = readContext;
        _actor = actor;
    }

    public async Task<CurrentUserDto> Handle(GetCurrentUserQuery request, CancellationToken cancellationToken)
    {
        var userId = _actor.RequireUserId();

        var user = await _readContext.Users
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new NotFoundException(nameof(User), userId);

        var tenant = await _readContext.Tenants
            .FirstAsync(t => t.Id == user.TenantId, cancellationToken);

        return new CurrentUserDto(
            user.Id, user.FullName, user.Email, user.Role,
            tenant.Id, tenant.Name, user.WalletBalance, tenant.Currency);
    }
}
