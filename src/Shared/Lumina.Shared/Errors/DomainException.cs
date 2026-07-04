namespace Lumina.Shared.Errors;

/// <summary>
/// Base type for domain-level exceptions that controllers may safely surface
/// to the caller (HTTP 4xx). Only the static <see cref="Message"/> of these
/// exceptions is considered safe to return — the constructor message is
/// authored by application code, never a driver/stack frame.
///
/// <para>Unbounded <see cref="Exception"/>s (DB driver faults, I/O errors,
/// framework exceptions) MUST NOT be returned to clients. Controllers catch
/// <see cref="DomainException"/>s for structured 4xx responses and fall back to
/// a fixed generic message for everything else, logging the full exception
/// server-side. This keeps stack hints, file paths, and SQL errors off the
/// wire (SEC-022).</para>
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The referenced entity does not exist. Controllers map this to HTTP 404.
/// </summary>
public class NotFoundException : DomainException
{
    public NotFoundException(string message) : base(message) { }
    public NotFoundException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The request conflicts with the current state of the resource (e.g. a
/// duplicate name on a unique index). Controllers map this to HTTP 409.
/// </summary>
public class ConflictException : DomainException
{
    public ConflictException(string message) : base(message) { }
    public ConflictException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The request failed a domain validation rule that is not expressible as a
/// data-annotation (e.g. a pipeline cannot be deleted while builds are
/// running). Controllers map this to HTTP 400.
/// </summary>
public class ValidationException : DomainException
{
    public ValidationException(string message) : base(message) { }
    public ValidationException(string message, Exception innerException) : base(message, innerException) { }
}
