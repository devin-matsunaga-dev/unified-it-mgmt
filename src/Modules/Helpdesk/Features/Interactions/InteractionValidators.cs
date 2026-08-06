using FluentValidation;

namespace Modules.Helpdesk.Features.Interactions;

public sealed class CreateCommentRequestValidator : AbstractValidator<CreateCommentRequest>
{
    public CreateCommentRequestValidator()
    {
        RuleFor(request => request.Body).NotEmpty().MaximumLength(10_000);
    }
}

public sealed class CreateWorklogRequestValidator : AbstractValidator<CreateWorklogRequest>
{
    public CreateWorklogRequestValidator()
    {
        RuleFor(request => request.Minutes).InclusiveBetween(1, 1440);
        RuleFor(request => request.Note).MaximumLength(2_000);
    }
}
