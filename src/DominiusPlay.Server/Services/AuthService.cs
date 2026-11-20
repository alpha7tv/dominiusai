using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace DominiusPlay.Server.Services;

public class JwtOptions
{
    public required SymmetricSecurityKey Key { get; init; }
    public string Issuer { get; init; } = "DominiusPlay.Crm";
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(12);
}

public class AuthService(CrmDbContext db, JwtOptions jwt)
{
    public LoginResponse Issue(User u)
    {
        var exp = DateTimeOffset.UtcNow.Add(jwt.Lifetime);
        var token = new JwtSecurityToken(jwt.Issuer, jwt.Issuer,
            [new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim(ClaimTypes.Name, u.UserName), new Claim(ClaimTypes.Role, u.Role.ToString())],
            expires: exp.UtcDateTime, signingCredentials: new SigningCredentials(jwt.Key, SecurityAlgorithms.HmacSha256));
        return new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token), u.DisplayName, u.Role, exp);
    }

    public async Task<User?> CurrentAsync(ClaimsPrincipal p, CancellationToken ct = default)
    {
        var id = p.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var g) ? await db.Users.FirstOrDefaultAsync(u => u.Id == g && u.Active, ct) : null;
    }

    /// <summary>Chave de assinatura persistida no diretório de dados (gerada no primeiro uso).</summary>
    public static SymmetricSecurityKey LoadOrCreateKey(string dataDir)
    {
        var path = Path.Combine(dataDir, "jwt.key");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        }
        return new SymmetricSecurityKey(File.ReadAllBytes(path));
    }
}
