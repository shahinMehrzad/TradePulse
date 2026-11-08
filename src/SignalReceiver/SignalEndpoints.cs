using Application.Signals;
using Microsoft.AspNetCore.Http.HttpResults;

namespace SignalReceiver;

internal static class SignalEndpoints
{
    public const string Path = "/api/signals";

    /// <summary>
    /// <c>POST /api/signals</c>: the webhook of the program that makes signals. The answers, which the sender acts on:
    /// <c>202</c> taken (a paper trade is open), <c>200</c> sent before and taken, <c>422</c> refused (with the reason; never send it again),
    /// <c>400</c> not the contract, <c>401</c> not proven to be the sender, <c>413</c> too large, <c>503</c> not judged (no recent price, or the
    /// database is down; send it again, same body, a new time and signature).
    /// </summary>
    public static IEndpointRouteBuilder MapSignals(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, TakeAsync);
        app.MapGet("/health", () => Results.Ok("ok"));
        return app;
    }

    private static async Task<IResult> TakeAsync(HttpRequest request, SignalIntake intake, SignalIntakeOptions options, CancellationToken cancellationToken)
    {
        // Never read more than a signal can be: a body of any size is not made to wait in memory for a signature to be checked.
        var body = await ReadAsync(request.Body, options.MaxBodyBytes, cancellationToken);
        if (body is null)
            return Results.Json(new { error = "The body is too large." }, statusCode: StatusCodes.Status413PayloadTooLarge);

        var result = await intake.TakeAsync(
            body, request.Headers["X-Timestamp"].FirstOrDefault(), request.Headers["X-Signature"].FirstOrDefault(),
            request.Headers["Idempotency-Key"].FirstOrDefault(), cancellationToken);

        return result switch
        {
            IntakeResult.Accepted accepted => Results.Json(new { signalId = accepted.SignalId, tradeId = accepted.TradeId }, statusCode: StatusCodes.Status202Accepted),
            IntakeResult.Duplicate duplicate => Results.Json(new { signalId = duplicate.SignalId, status = "duplicate" }),
            IntakeResult.Rejected rejected => Results.Json(new { signalId = rejected.SignalId, reason = rejected.Reason.ToString() }, statusCode: StatusCodes.Status422UnprocessableEntity),
            IntakeResult.Malformed malformed => Results.Json(new { error = malformed.Error }, statusCode: StatusCodes.Status400BadRequest),
            IntakeResult.Unauthorized => Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized),
            IntakeResult.NoPrice noPrice => TryAgain(new { signalId = noPrice.SignalId, error = "no_recent_price" }, seconds: 5),
            _ => TryAgain(new { error = "unavailable" }, seconds: 10),
        };
    }

    private static IResult TryAgain(object body, int seconds) => new RetryResult(body, seconds);

    /// <summary>503 with <c>Retry-After</c>: not a verdict on the signal, only "not now".</summary>
    private sealed class RetryResult(object body, int seconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(httpContext);
        }
    }

    /// <summary>The body as text, or null when it is longer than <paramref name="limit"/> bytes.</summary>
    private static async Task<string?> ReadAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[limit + 1];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
                break;

            length += read;
        }

        return length > limit ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }
}
