namespace Application.Dtos.Auth;

public enum LoginStage
{
    /// <summary>Done: <see cref="LoginStepDto.Session"/> holds the tokens.</summary>
    Authenticated,
    /// <summary>The password was right and the user has two-factor on: send the code with the challenge token.</summary>
    TwoFactorRequired,
    /// <summary>The user has to change the password and/or enrol two-factor first; see the two flags.</summary>
    SetupRequired
}

public record LoginStepDto(LoginStage Stage, AuthResponseDto? Session, string? ChallengeToken, bool MustChangePassword, bool NeedsTwoFactorSetup, bool EmailCodeAvailable = false);

public record TwoFactorEnrollmentDto(string SharedKey, string AuthenticatorUri);

/// <summary>Two-factor is on and the user is signed in; the recovery codes are shown this once.</summary>
public record TwoFactorEnrolledDto(AuthResponseDto Session, IReadOnlyList<string> RecoveryCodes);
