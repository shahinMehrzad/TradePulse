namespace Domain.Exceptions;

public sealed class ConfigurationValidationException(string message) : Exception(message);
