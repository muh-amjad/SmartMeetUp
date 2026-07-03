using FluentValidation;
using MeetUp.Api.Dtos.Auth;

namespace MeetUp.Api.Validators;

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequestDto>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty().WithMessage("Refresh token is required.");
    }
}
