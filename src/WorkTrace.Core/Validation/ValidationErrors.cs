namespace WorkTrace.Core.Validation;

/// <summary>Field-level validation errors keyed by input property name.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool IsValid => _errors.Count == 0;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ByField =>
        _errors.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)e.Value, StringComparer.Ordinal);

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            _errors[field] = list = [];
        }

        list.Add(message);
    }

    public string? For(string field) => _errors.TryGetValue(field, out var list) ? list[0] : null;

    /// <summary>Throws <see cref="DomainValidationException"/> when any error was recorded.</summary>
    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new DomainValidationException(this);
        }
    }
}

/// <summary>Raised by services when input violates domain rules. Nothing has been persisted.</summary>
public sealed class DomainValidationException(ValidationErrors errors)
    : Exception(string.Join(" ", errors.ByField.SelectMany(e => e.Value)))
{
    public ValidationErrors Errors { get; } = errors;

    public static DomainValidationException ForField(string field, string message)
    {
        var errors = new ValidationErrors();
        errors.Add(field, message);
        return new DomainValidationException(errors);
    }
}

/// <summary>Raised when an operation refers to a record that does not exist (any more).</summary>
public sealed class EntityNotFoundException(string entity, int id)
    : Exception($"{entity} {id} was not found. It may have been deleted.");

/// <summary>Raised when an operation is not allowed in the record's current state (e.g. deleting a used client).</summary>
public sealed class OperationNotAllowedException(string message) : Exception(message);
