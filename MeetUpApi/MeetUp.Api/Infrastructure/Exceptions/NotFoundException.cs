namespace MeetUp.Api.Infrastructure.Exceptions;

public sealed class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message)
    {
    }

    public NotFoundException(string resourceName, string resourceId)
        : base($"{resourceName} with ID '{resourceId}' was not found.")
    {
    }
}
