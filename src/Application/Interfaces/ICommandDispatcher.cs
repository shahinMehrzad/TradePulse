namespace Application.Interfaces;

public interface ICommandDispatcher
{
    Task<TResponse> SendAsync<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default);
}
