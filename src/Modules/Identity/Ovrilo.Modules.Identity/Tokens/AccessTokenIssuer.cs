using System.Security.Claims;
using System.Text;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using Ovrilo.Modules.Identity.Contracts;

namespace Ovrilo.Modules.Identity.Tokens;

internal sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

internal sealed class AccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Issue(Guid userId, Guid sessionId)
    {
        var jwt = options.Value;
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(jwt.AccessTokenLifetimeMinutes);

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(IdentityClaims.UserId, userId.ToString()),
                new Claim(IdentityClaims.SessionId, sessionId.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            SigningCredentials = new SigningCredentials(CreateSigningKey(jwt), SecurityAlgorithms.HmacSha256),
        });

        return new AccessToken(token, expiresAt);
    }

    public static SymmetricSecurityKey CreateSigningKey(JwtOptions jwt) => new(Encoding.UTF8.GetBytes(jwt.SigningKey));
}
