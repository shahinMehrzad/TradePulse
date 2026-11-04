namespace Application.Dtos.Auth;

/// <summary>
/// What every step of the sign-in answers with. <paramref name="Stage"/> is a <see cref="LoginStage"/> name. Only the
/// <c>Authenticated</c> stage has tokens; the other two carry a <paramref name="ChallengeToken"/> for the next call.
/// <paramref name="RecoveryCodes"/> is set once, when two-factor was just turned on. <paramref name="EmailCodeAvailable"/> is true at the code step when the user may ask for a code by email.
/// <paramref name="ElevationToken"/> is set when the user signed in with a code from the authenticator app or by email: that code already is the step-up (see <c>StepUpCommandHandler</c>), so they are not asked for another at once.
/// </summary>
public record LoginResponseDto(
    string Stage,
    string? AccessToken,
    DateTime? ExpiresAt,
    string? UserName,
    string? RefreshToken,
    string? ChallengeToken,
    bool MustChangePassword,
    bool NeedsTwoFactorSetup,
    IReadOnlyList<string>? RecoveryCodes = null,
    bool EmailCodeAvailable = false,
    string? ElevationToken = null,
    DateTime? ElevationExpiresAt = null);
