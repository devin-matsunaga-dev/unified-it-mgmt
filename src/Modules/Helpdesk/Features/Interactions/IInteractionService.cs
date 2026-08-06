using System.Security.Claims;

using Microsoft.AspNetCore.Http;

namespace Modules.Helpdesk.Features.Interactions;

public interface IInteractionService
{
    Task<InteractionResult<CommentResponse>> AddCommentAsync(
        Guid ticketId, CreateCommentRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<CommentResponse>?> GetCommentsAsync(
        Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<InteractionResult<WorklogResponse>> AddWorklogAsync(
        Guid ticketId, CreateWorklogRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<InteractionResult<IReadOnlyList<WorklogResponse>>> GetWorklogsAsync(
        Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<InteractionResult<AttachmentResponse>> AddAttachmentAsync(
        Guid ticketId, IFormFile file, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<AttachmentResponse>?> GetAttachmentsAsync(
        Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<InteractionResult<AttachmentDownload>> DownloadAttachmentAsync(
        Guid ticketId, Guid attachmentId, ClaimsPrincipal actor, CancellationToken cancellationToken);
}
