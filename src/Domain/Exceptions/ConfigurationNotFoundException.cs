namespace Domain.Exceptions;

/// <summary>There is no configuration with the requested id (it never existed, or somebody deleted it meanwhile).</summary>
public sealed class ConfigurationNotFoundException(string message) : Exception(message);
