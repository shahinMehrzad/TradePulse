using Application.Interfaces;
using Domain.Repositories;
using System.Security.Cryptography;

namespace Application.Commands.Auth;

public class GenerateRefreshTokenCommandHandler(IRefreshTokenRepository refreshTokenRepository) : ICommandHandler<GenerateRefreshTokenCommand, string>
{
    private readonly IRefreshTokenRepository _refreshTokenRepository = refreshTokenRepository;

    public async Task<string> HandleAsync(GenerateRefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var randomNumber = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        var refreshToken = Convert.ToBase64String(randomNumber);

        await _refreshTokenRepository.AddAsync(new Domain.Entities.RefreshToken
        {
            Token = refreshToken,
            UserId = request.UserId,
            DeviceId = request.DeviceId,
            IPAddress = request.IpAddress,
            UserAgent = request.UserAgent,
            ExpiresOn = DateTime.UtcNow.AddDays(request.DurationInDays)
        }, cancellationToken);

        return refreshToken;
    }
}
