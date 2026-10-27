namespace Application.Dtos.CandleWorkerConfigurations;

public sealed record LateStartSeries(int Id, DateTime StartDate, DateTime FirstCandle);
