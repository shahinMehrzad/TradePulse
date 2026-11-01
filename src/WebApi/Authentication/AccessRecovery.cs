using Application.Commands.Users;
using Application.Interfaces;
using Domain.Exceptions;

namespace WebApi.Authentication;

/// <summary>
/// <c>dotnet WebApi.dll reset-access admin</c>: the way back in when the only administrator has lost both the phone and the recovery codes.
/// It needs a shell on the server, which is the point: whoever has that has the database and the signing key anyway.
/// The one-time password is printed to the console only, never logged, and has to be changed at the next sign-in, followed by a new two-factor setup.
/// </summary>
public static class AccessRecovery
{
    public static async Task<int> RunAsync(IServiceProvider services, string userNameOrEmail)
    {
        using var scope = services.CreateScope();
        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<ICommandDispatcher>()
                .SendAsync<RecoverAccessCommand, TemporaryPasswordDto>(new RecoverAccessCommand(userNameOrEmail), CancellationToken.None);

            Console.WriteLine($"Access recovered for '{result.UserName}'.");
            Console.WriteLine($"One-time password: {result.TemporaryPassword}");
            Console.WriteLine("Sign in with it; you will choose a new password and set up two-factor authentication again.");
            return 0;
        }
        catch (ConfigurationNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
