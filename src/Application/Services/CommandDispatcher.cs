using Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Services
{
    public class CommandDispatcher(IServiceProvider serviceProvider) : ICommandDispatcher
    {
        public async Task<TResponse> SendAsync<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        {
            var handler = serviceProvider.GetRequiredService<ICommandHandler<TRequest, TResponse>>();
            return await handler.HandleAsync(request, cancellationToken);
        }
    }
}
