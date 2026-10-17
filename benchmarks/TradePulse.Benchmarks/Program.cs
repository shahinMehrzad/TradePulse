using BenchmarkDotNet.Running;
using TradePulse.Benchmarks;

// Release only:  dotnet run -c Release --project benchmarks/TradePulse.Benchmarks
// Needs Docker (a throwaway Postgres is started and seeded).
BenchmarkSwitcher.FromAssembly(typeof(CandleReadBenchmarks).Assembly).Run(args);
