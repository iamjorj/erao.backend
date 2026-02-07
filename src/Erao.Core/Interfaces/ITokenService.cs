using Erao.Core.Entities;

namespace Erao.Core.Interfaces;

public interface ITokenService
{
    string GenerateAccessToken(User user);
    string GenerateRefreshToken();
    bool ValidateAccessToken(string token);
    Guid? GetUserIdFromToken(string token);
}
