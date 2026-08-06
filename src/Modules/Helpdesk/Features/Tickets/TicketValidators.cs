using FluentValidation;

namespace Modules.Helpdesk.Features.Tickets;

public sealed class CreateTicketRequestValidator : AbstractValidator<CreateTicketRequest>
{
    public CreateTicketRequestValidator()
    {
        RuleFor(request => request.Title).NotEmpty().MaximumLength(200);
        RuleFor(request => request.Description).NotEmpty().MaximumLength(10_000);
        RuleFor(request => request.Type).IsInEnum();
        RuleFor(request => request.Urgency).IsInEnum();
        RuleFor(request => request.Impact).IsInEnum();
        RuleFor(request => request.RequesterId).MaximumLength(200);
    }
}

public sealed class TransitionTicketRequestValidator : AbstractValidator<TransitionTicketRequest>
{
    public TransitionTicketRequestValidator()
    {
        RuleFor(request => request.TargetStatus).NotEmpty().MaximumLength(50);
        RuleFor(request => request.ResolutionNote).MaximumLength(10_000);
        RuleFor(request => request.ResolutionNote)
            .NotEmpty()
            .When(request => string.Equals(request.TargetStatus, "Resolved", StringComparison.OrdinalIgnoreCase))
            .WithMessage("A resolution note is required when resolving a ticket.");
    }
}

public sealed class UpdateTicketRequestValidator : AbstractValidator<UpdateTicketRequest>
{
    public UpdateTicketRequestValidator()
    {
        RuleFor(request => request.Title).NotEmpty().MaximumLength(200);
        RuleFor(request => request.Description).NotEmpty().MaximumLength(10_000);
        RuleFor(request => request.Type).IsInEnum();
        RuleFor(request => request.Urgency).IsInEnum();
        RuleFor(request => request.Impact).IsInEnum();
    }
}
