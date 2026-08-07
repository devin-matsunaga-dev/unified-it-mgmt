namespace Modules.Helpdesk.Features.Interactions;

public sealed record CreateCommentRequest(string Body, bool IsInternal);
public sealed record CommentResponse(
    Guid Id, Guid TicketId, string Body, bool IsInternal, string AuthorId, string AuthorName, DateTimeOffset CreatedAt);

public sealed record CreateWorklogRequest(int Minutes, string? Note);
public sealed record WorklogResponse(
    Guid Id, Guid TicketId, int Minutes, string? Note, string AuthorId, DateTimeOffset CreatedAt);

public sealed record AttachmentResponse(
    Guid Id,
    Guid TicketId,
    string FileName,
    string ContentType,
    long Size,
    string UploadedById,
    DateTimeOffset CreatedAt,
    string DownloadUrl);

public enum InteractionOutcome
{
    Success,
    NotFound,
    Forbidden,
    InvalidFile,
    ScanRejected,
}

public sealed record InteractionResult<T>(InteractionOutcome Outcome, T? Value = default, string? Error = null);
public sealed record AttachmentDownload(string FileName, string ContentType, byte[] Content);
