using Application.Interfaces;
using Application.Services;
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
            Token = RefreshTokenHasher.Hash(refreshToken),
            UserId = request.UserId,
            DeviceId = request.DeviceId ?? string.Empty,
            IPAddress = request.IpAddress ?? string.Empty,
            UserAgent = request.UserAgent ?? string.Empty,
            ExpiresOn = DateTime.UtcNow.AddDays(request.DurationInDays)
        }, cancellationToken);

        return refreshToken;
    }
}
