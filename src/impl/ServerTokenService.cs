#if NET9_0
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace MyConnection;

public class ServerTokenService
{
    private readonly JwtSecurityTokenHandler _handler;
    private readonly SigningCredentials _credentials;
    private readonly TokenValidationParameters _validationParams;

    public ServerTokenService(ServerConfig config)
    {
        _handler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(config.jwtSecret));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        _validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = config.jwtIssuer,
            ValidAudience = config.jwtAudience,
            IssuerSigningKey = key
        };
    }

    public string CreateToken(string id, string name)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, id),
            new Claim(JwtRegisteredClaimNames.Name, name),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(24),
            SigningCredentials = _credentials,
            Issuer = _validationParams.ValidIssuer,
            Audience = _validationParams.ValidAudience
        };
        var token = _handler.CreateToken(descriptor);
        return _handler.WriteToken(token);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            return _handler.ValidateToken(token, _validationParams, out _);
        }
        catch
        {
            return null;
        }
    }
}
#endif
