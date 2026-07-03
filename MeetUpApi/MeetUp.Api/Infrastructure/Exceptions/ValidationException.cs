namespace MeetUp.Api.Infrastructure.Exceptions;

public sealed class ValidationException : Exception
{
    public IDictionary<string, string[]> Errors { get; }

    public ValidationException(IDictionary<string, string[]> errors)
        : base("Validation failed.")
    {
        Errors = errors;
    }

    public ValidationException(string fieldName, string[] errors)
        : base("Validation failed.")
    {
        Errors = new Dictionary<string, string[]> { { fieldName, errors } };
    }
}
