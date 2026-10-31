namespace EscrowMarketplace.Domain.Exceptions;

public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception innerException) : base(message, innerException) { }
}

public class InvalidStateTransitionException : DomainException
{
    public InvalidStateTransitionException(string entity, string fromState, string toState)
        : base($"Invalid state transition for {entity} from '{fromState}' to '{toState}'.") { }
}

public class DoubleBookingExclusionException : DomainException
{
    public DoubleBookingExclusionException(string message) : base(message) { }
}

public class NotFoundException : DomainException
{
    public NotFoundException(string entity, object key)
        : base($"Entity '{entity}' with key '{key}' was not found.") { }
}

/// <summary>The caller is authenticated but may not perform this action. Mapped to HTTP 403.</summary>
public class ForbiddenAccessException : DomainException
{
    public ForbiddenAccessException(string message) : base(message) { }
}

public class InsufficientFundsException : DomainException
{
    public InsufficientFundsException(decimal available, decimal required)
        : base($"Insufficient wallet balance: {available:0.00} available, {required:0.00} required.") { }
}
