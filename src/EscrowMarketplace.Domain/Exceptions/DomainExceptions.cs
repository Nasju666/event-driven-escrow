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

public class ConcurrencyConflictException : DomainException
{
    public ConcurrencyConflictException(string message) : base(message) { }
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
