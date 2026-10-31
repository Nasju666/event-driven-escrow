using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EscrowMarketplace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace EscrowMarketplace.Api.Controllers;

/// <summary>
/// DEMO LOGIN ONLY. Issues a JWT for a seeded user by email, with no password, so reviewers can
/// switch between buyer, provider and admin in one click. Controlled by Demo:PasswordlessLogin;
/// when it is false these endpoints return 404. A real deployment would put an identity provider
/// (Entra ID, Auth0, Keycloak) here, and the rest of the API would not change because it only
/// trusts validated tokens.
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly MarketplaceDbContext _context;
    private readonly IConfiguration _configuration;

    public AuthController(MarketplaceDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    private bool DemoLoginEnabled => _configuration.GetValue<bool>("Demo:PasswordlessLogin");

    [HttpPost("token")]
    [ProducesResponseType(typeof(AuthTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuthTokenResponse>> GetToken([FromBody] TokenRequest request, CancellationToken ct)
    {
        if (!DemoLoginEnabled) return NotFound();

        // Login happens before a tenant is known, so this is one of the few cross-tenant reads.
        var user = await _context.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == request.Email && u.IsActive, ct);

        if (user == null)
        {
            return NotFound(new ProblemDetails { Title = "User Not Found", Detail = $"No demo user with email '{request.Email}'." });
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Secret"]!));
        var expires = DateTime.UtcNow.AddHours(2);

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new(JwtRegisteredClaimNames.Email, user.Email),
                new(ClaimTypes.Name, user.FullName),
                new(ClaimTypes.Role, user.Role.ToString()),
                new("tenant_id", user.TenantId.ToString())
            },
            expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return Ok(new AuthTokenResponse(
            new JwtSecurityTokenHandler().WriteToken(token),
            user.Id, user.Email, user.FullName, user.Role.ToString(), user.TenantId, expires));
    }

    /// <summary>Seeded users for the demo login screen.</summary>
    [HttpGet("demo-users")]
    public async Task<ActionResult<IReadOnlyList<DemoUserSummary>>> GetDemoUsers(CancellationToken ct)
    {
        if (!DemoLoginEnabled) return NotFound();

        var users = await _context.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.IsActive)
            .Join(_context.Tenants, u => u.TenantId, t => t.Id,
                (u, t) => new DemoUserSummary(u.Id, u.FullName, u.Email, u.Role.ToString(), u.TenantId, t.Name))
            .ToListAsync(ct);

        return Ok(users.OrderBy(u => u.TenantName).ThenBy(u => u.Role).ToList());
    }
}

public record TokenRequest(string Email);
public record AuthTokenResponse(string Token, Guid UserId, string Email, string FullName, string Role, Guid TenantId, DateTime ExpiresAt);
public record DemoUserSummary(Guid Id, string FullName, string Email, string Role, Guid TenantId, string TenantName);
