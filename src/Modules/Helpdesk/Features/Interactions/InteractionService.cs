using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Modules.Helpdesk.Data;
using Platform.Auditing;

namespace Modules.Helpdesk.Features.Interactions;

public sealed class InteractionService(
    HelpdeskDbContext dbContext,
    IAttachmentStorage attachmentStorage,
    IAntivirusScanner antivirusScanner,
    IAuditService auditService) : IInteractionService
{
    public const long MaximumAttachmentSize = 25 * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".doc", ".docx", ".gif", ".jpeg", ".jpg", ".pdf", ".png", ".txt", ".xls", ".xlsx", ".zip",
    };
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/msword",
        "application/pdf",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/zip",
        "image/gif",
        "image/jpeg",
        "image/png",
        "text/csv",
        "text/plain",
    };

    public async Task<InteractionResult<CommentResponse>> AddCommentAsync(
        Guid ticketId, CreateCommentRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!await VisibleTickets(actor).AnyAsync(ticket => ticket.Id == ticketId, cancellationToken))
        {
            return new(InteractionOutcome.NotFound);
        }

        if (request.IsInternal && IsEndUser(actor))
        {
            return new(InteractionOutcome.Forbidden, Error: "End users cannot create internal comments.");
        }

        var comment = new TicketComment
        {
            Id = Guid.CreateVersion7(), TicketId = ticketId, Body = request.Body.Trim(),
            IsInternal = request.IsInternal, AuthorId = ActorId(actor), CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.TicketComments.Add(comment);
        await dbContext.SaveChangesAsync(cancellationToken);
        var response = Map(comment);
        await auditService.WriteAsync(
            actor, "Created", "TicketComment", comment.Id.ToString(), null, response, cancellationToken);
        return new(InteractionOutcome.Success, response);
    }

    public async Task<IReadOnlyList<CommentResponse>?> GetCommentsAsync(
        Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!await VisibleTickets(actor).AnyAsync(ticket => ticket.Id == ticketId, cancellationToken))
        {
            return null;
        }

        var query = dbContext.TicketComments.Where(comment => comment.TicketId == ticketId);
        if (IsEndUser(actor))
        {
            query = query.Where(comment => !comment.IsInternal);
        }

        return await query.OrderBy(comment => comment.CreatedAt).ThenBy(comment => comment.Id)
            .Select(comment => new CommentResponse(
                comment.Id, comment.TicketId, comment.Body, comment.IsInternal, comment.AuthorId, comment.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<InteractionResult<WorklogResponse>> AddWorklogAsync(
        Guid ticketId, CreateWorklogRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (IsEndUser(actor))
        {
            return new(InteractionOutcome.Forbidden, Error: "End users cannot create worklogs.");
        }

        if (!await VisibleTickets(actor).AnyAsync(ticket => ticket.Id == ticketId, cancellationToken))
        {
            return new(InteractionOutcome.NotFound);
        }

        var worklog = new TicketWorklog
        {
            Id = Guid.CreateVersion7(), TicketId = ticketId, Minutes = request.Minutes,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            AuthorId = ActorId(actor), CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.TicketWorklogs.Add(worklog);
        await dbContext.SaveChangesAsync(cancellationToken);
        var response = Map(worklog);
        await auditService.WriteAsync(actor, "Created", "TicketWorklog", worklog.Id.ToString(), null, response, cancellationToken);
        return new(InteractionOutcome.Success, response);
    }

    public async Task<InteractionResult<IReadOnlyList<WorklogResponse>>> GetWorklogsAsync(
        Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (IsEndUser(actor))
        {
            return new(InteractionOutcome.Forbidden, Error: "End users cannot read worklogs.");
        }

        if (!await VisibleTickets(actor).AnyAsync(ticket => ticket.Id == ticketId, cancellationToken))
        {
            return new(InteractionOutcome.NotFound);
        }

        var worklogs = await dbContext.TicketWorklogs.Where(worklog => worklog.TicketId == ticketId)
            .OrderBy(worklog => worklog.CreatedAt).ThenBy(worklog => worklog.Id)
            .Select(worklog => new WorklogResponse(
                worklog.Id, worklog.TicketId, worklog.Minutes, worklog.Note, worklog.AuthorId, worklog.CreatedAt))
            .ToListAsync(cancellationToken);
        return new(InteractionOutcome.Success, worklogs);
    }

    public async Task<InteractionResult<AttachmentResponse>> AddAttachmentAsync(
        Guid ticketId, IFormFile file, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!await VisibleTickets(actor).AnyAsync(ticket => ticket.Id == ticketId, cancellationToken))
        {
            return new(InteractionOutcome.NotFound);
        }

        var fileName = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(fileName) || file.Length is <= 0 or > MaximumAttachmentSize
            || !AllowedExtensions.Contains(extension) || !AllowedContentTypes.Contains(file.ContentType))
        {
            return new(
                InteractionOutcome.InvalidFile,
                Error: $"Files must be non-empty, no larger than {MaximumAttachmentSize / 1024 / 1024} MB, and use an allowed type.");
        }

        await using var content = new MemoryStream(checked((int)file.Length));
        await file.CopyToAsync(content, cancellationToken);
        content.Position = 0;
        var scan = await antivirusScanner.ScanAsync(content, fileName, cancellationToken);
        if (!scan.IsSafe)
        {
            return new(InteractionOutcome.ScanRejected, Error: scan.Reason ?? "The attachment failed antivirus scanning.");
        }

        content.Position = 0;
        var attachment = new TicketAttachment
        {
            Id = Guid.CreateVersion7(), TicketId = ticketId, FileName = fileName, ContentType = file.ContentType,
            Size = file.Length, UploadedById = ActorId(actor), CreatedAt = DateTimeOffset.UtcNow,
        };
        attachment.ObjectKey = $"tickets/{ticketId}/{attachment.Id}{extension.ToLowerInvariant()}";
        await attachmentStorage.PutAsync(
            attachment.ObjectKey, content, attachment.Size, attachment.ContentType, cancellationToken);
        dbContext.TicketAttachments.Add(attachment);
        await dbContext.SaveChangesAsync(cancellationToken);
        var response = Map(attachment);
        await auditService.WriteAsync(
            actor, "Uploaded", "TicketAttachment", attachment.Id.ToString(), null, response, cancellationToken);
        return new(InteractionOutcome.Success, response);
    }

    public async Task<IReadOnlyList<AttachmentResponse>?> GetAttachmentsAsync(
        Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!await VisibleTickets(actor).AnyAsync(ticket => ticket.Id == ticketId, cancellationToken))
        {
            return null;
        }

        return await dbContext.TicketAttachments.Where(attachment => attachment.TicketId == ticketId)
            .OrderBy(attachment => attachment.CreatedAt).ThenBy(attachment => attachment.Id)
            .Select(attachment => new AttachmentResponse(
                attachment.Id, attachment.TicketId, attachment.FileName, attachment.ContentType,
                attachment.Size, attachment.UploadedById, attachment.CreatedAt,
                $"/api/tickets/{attachment.TicketId}/attachments/{attachment.Id}"))
            .ToListAsync(cancellationToken);
    }

    public async Task<InteractionResult<AttachmentDownload>> DownloadAttachmentAsync(
        Guid ticketId, Guid attachmentId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!await VisibleTickets(actor).AnyAsync(ticket => ticket.Id == ticketId, cancellationToken))
        {
            return new(InteractionOutcome.NotFound);
        }

        var attachment = await dbContext.TicketAttachments.SingleOrDefaultAsync(
            item => item.Id == attachmentId && item.TicketId == ticketId, cancellationToken);
        if (attachment is null)
        {
            return new(InteractionOutcome.NotFound);
        }

        var content = await attachmentStorage.GetAsync(attachment.ObjectKey, cancellationToken);
        return new(InteractionOutcome.Success, new AttachmentDownload(
            attachment.FileName, attachment.ContentType, content));
    }

    private IQueryable<Ticket> VisibleTickets(ClaimsPrincipal actor)
    {
        var query = dbContext.Tickets.AsQueryable();
        return IsEndUser(actor) ? query.Where(ticket => ticket.RequesterId == ActorId(actor)) : query;
    }

    private static bool IsEndUser(ClaimsPrincipal actor) => actor.IsInRole("EndUser");
    private static string ActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");

    private static CommentResponse Map(TicketComment comment) => new(
        comment.Id, comment.TicketId, comment.Body, comment.IsInternal, comment.AuthorId, comment.CreatedAt);
    private static WorklogResponse Map(TicketWorklog worklog) => new(
        worklog.Id, worklog.TicketId, worklog.Minutes, worklog.Note, worklog.AuthorId, worklog.CreatedAt);
    private static AttachmentResponse Map(TicketAttachment attachment) => new(
        attachment.Id, attachment.TicketId, attachment.FileName, attachment.ContentType,
        attachment.Size, attachment.UploadedById, attachment.CreatedAt,
        $"/api/tickets/{attachment.TicketId}/attachments/{attachment.Id}");
}
