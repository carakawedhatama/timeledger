namespace TimeLedger.Domain;

/// <summary>A business rule was violated. The API maps this to HTTP 422.</summary>
public sealed class DomainException(string message) : Exception(message);
