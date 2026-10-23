using Domain.Entities;
using Domain.Enums;

namespace Application.Exchange;

/// <summary>Funding rate settlements from an exchange's request/response API (perpetual futures only).</summary>
public interface IFundingRateGateway
{
    /// <summary>
    /// One page of settlements of <paramref name="symbol"/>, oldest first, at or after <paramref name="from"/>. The exchange keeps
    /// the full history. <see cref="ExchangePage{T}.HasMore"/> says whether to ask again from after the last one.
    /// </summary>
    Task<ExchangeResult<ExchangePage<FundingRate>>> GetFundingRatesAsync(
        ExchangeEnum exchange, SymbolEnum symbol, DateTime from, CancellationToken cancellationToken);
}
