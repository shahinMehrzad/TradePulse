using Application.Dtos;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Queries.AdminConfigurations;

public class GetPaginationCandleWorkerConfigurationsQuery() : PaginationRequestDto
{
    public string? Keyword { get; set; }
}

public sealed class GetPaginationCandleWorkerConfigurationQueryHandler(ICandleWorkerConfigurationRepository workerConfigurationRepository)
    : ICommandHandler<GetPaginationCandleWorkerConfigurationsQuery, PaginationResponseDto<CandleWorkerConfiguration>>
{
    private readonly ICandleWorkerConfigurationRepository _workerConfigurationRepository = workerConfigurationRepository;
    public async Task<PaginationResponseDto<CandleWorkerConfiguration>> HandleAsync(GetPaginationCandleWorkerConfigurationsQuery request, CancellationToken cancellationToken)
    {
        if (request.PageNumber <= 0)
            throw new ConfigurationValidationException("Page Number must be greater than zero.");
        if (request.PageSize is <= 0 or > 100)
            throw new ConfigurationValidationException("Page Size must be between 1 and 100.");

        var res = await _workerConfigurationRepository.GetConfigurationsListByPagination(request.PageNumber, request.PageSize, request.Keyword, cancellationToken);
        return new PaginationResponseDto<CandleWorkerConfiguration>
        {
            TotalItemsCount = res.totalCount,
            TotalPages = (int)Math.Ceiling((double)res.totalCount / request.PageSize),
            ItemsPerPage = request.PageSize,
            CurrentPage = request.PageNumber,
            Data = res.result
        };
    }
}
