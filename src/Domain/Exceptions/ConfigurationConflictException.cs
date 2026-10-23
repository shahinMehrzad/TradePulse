namespace Domain.Exceptions;

public sealed class ConfigurationConflictException(string message, Exception? inner = null) : Exception(message, inner);
