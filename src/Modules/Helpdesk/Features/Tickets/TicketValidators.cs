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
