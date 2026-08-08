namespace Modules.Assets.Data;

/// <summary>What the agreement is for. Warranty here means a purchased extension, not the
/// manufacturer warranty date carried on the CI itself.</summary>
public enum ContractType
{
    Support = 1,
    Warranty = 2,
    Maintenance = 3,
    Lease = 4,
    Subscription = 5,
}

/// <summary>
/// An agreement with a vendor that covers zero or more CIs. Dates are calendar dates rather than
/// instants: a contract ends on a day, not at a moment in a timezone.
/// </summary>
public sealed class Contract
{
    public Guid Id { get; set; }
    public Guid VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;
    public string ContractNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ContractType Type { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool AutoRenews { get; set; }
    public decimal? Cost { get; set; }
    public string? Currency { get; set; }

    // The contract's internal owner, snapshotted the same way WP-1.7 snapshots a ticket requester:
    // the email is the renewal notice's recipient and must survive the person leaving the directory.
    public Guid? OwnerUserId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerEmail { get; set; }

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<ConfigurationItem> Cis { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Which clock a notification was raised against.</summary>
public enum ContractNotificationSubject
{
    Contract = 1,
    Warranty = 2,
}

/// <summary>
/// One raised renewal/expiry notification. It is both the record an operator can read back and the
/// job's dedupe key: the unique index over (subject, subject id, due date, threshold) is what makes a
/// second run of the same day silent, while moving the due date starts a fresh notification cycle.
/// </summary>
public sealed class ContractNotification
{
    public Guid Id { get; set; }
    public ContractNotificationSubject Subject { get; set; }
    public Guid SubjectId { get; set; }
    public string SubjectName { get; set; } = string.Empty;
    public DateOnly DueDate { get; set; }

    /// <summary>The threshold that was crossed: 30 or 7 days out, or 0 for the expiry itself.</summary>
    public int ThresholdDays { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }
}
