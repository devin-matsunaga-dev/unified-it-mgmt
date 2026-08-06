using FluentValidation;

namespace Modules.Helpdesk.Features.Assignments;

public sealed class CreateTeamRequestValidator : AbstractValidator<CreateTeamRequest>
{
    public CreateTeamRequestValidator() => RuleFor(request => request.Name).NotEmpty().MaximumLength(100);
}

public sealed class AddTeamMemberRequestValidator : AbstractValidator<AddTeamMemberRequest>
{
    public AddTeamMemberRequestValidator() => RuleFor(request => request.TechnicianId).NotEmpty().MaximumLength(200);
}

public sealed class CreateQueueRequestValidator : AbstractValidator<CreateQueueRequest>
{
    public CreateQueueRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(100);
        RuleFor(request => request.TeamId).NotEmpty();
    }
}

public sealed class AssignTicketRequestValidator : AbstractValidator<AssignTicketRequest>
{
    public AssignTicketRequestValidator() => RuleFor(request => request.TechnicianId).NotEmpty().MaximumLength(200);
}
