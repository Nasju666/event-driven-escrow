using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EscrowMarketplace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace EscrowMarketplace.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly MarketplaceDbContext _context;
    private readonly IConfiguration _configuration;

    public AuthController(MarketplaceDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    [HttpPost("token")]
    [ProducesResponseType(typeof(AuthTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuthTokenResponse>> GetToken(
        [FromBody] TokenRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _context.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == request.Email, cancellationToken);

        if (user == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "User Not Found",
                Detail = $"No demo user found with email '{request.Email}'."
            });
        }

        var secret = _configuration["Jwt:Secret"] ?? "SuperSecretEnterpriseEscrowKey2026!MustBeAtLeast32Bytes";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("tenant_id", user.TenantId.ToString())
        };

        var expires = DateTime.UtcNow.AddHours(2);
        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"] ?? "EscrowMarketplace",
            audience: _configuration["Jwt:Audience"] ?? "EscrowMarketplaceClients",
            claims: claims,
            expires: expires,
            signingCredentials: creds
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return Ok(new AuthTokenResponse(
            Token: tokenString,
            UserId: user.Id,
            Email: user.Email,
            FullName: user.FullName,
            Role: user.Role.ToString(),
            TenantId: user.TenantId,
            ExpiresAt: expires
        ));
    }

    [HttpGet("demo-users")]
    [ProducesResponseType(typeof(IReadOnlyList<DemoUserSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DemoUserSummary>>> GetDemoUsers(CancellationToken cancellationToken)
    {
        var users = await _context.Users
            .AsNoTracking()
            .Select(u => new DemoUserSummary(u.Id, u.FullName, u.Email, u.Role.ToString(), u.TenantId))
            .ToListAsync(cancellationToken);

        return Ok(users);
    }
}

public record TokenRequest(string Email);
public record AuthTokenResponse(string Token, Guid UserId, string Email, string FullName, string Role, Guid TenantId, DateTime ExpiresAt);
public record DemoUserSummary(Guid Id, string FullName, string Email, string Role, Guid TenantId);
