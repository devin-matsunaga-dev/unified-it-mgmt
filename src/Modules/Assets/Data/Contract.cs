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
    /// <summary>
    /// The purchase order this was bought on, canonicalised as <c>PO - 22-0419</c>. Required and
    /// unique: it is how a line of spend is found again.
    /// </summary>
    public string PoNumber { get; set; } = string.Empty;

    /// <summary>
    /// The vendor's or the organisation's own reference for the agreement — <c>CUC-ADM-22-C008</c>.
    /// A different fact from the PO: one identifies the purchase, the other the contract it bought,
    /// and a single row can carry both. Optional, because plenty of purchases have no such code.
    /// </summary>
    public string? ContractNumber { get; set; }
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

    /// <summary>
    /// Which department the spend belongs to, chosen from the platform's own directory. Snapshotted
    /// by name beside the id for the same reason CI ownership is: the directory belongs to Platform,
    /// this module may not join to it, and a contract's record has to stay readable after a
    /// department is renamed or retired.
    /// </summary>
    public Guid? DepartmentId { get; set; }

    public string? DepartmentName { get; set; }

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

    /// <summary>A licence pool's own end date, notified on the same 30/7/0 thresholds (WP-4.4).</summary>
    License = 3,

    /// <summary>
    /// Not a clock at all: a product installed on more devices than its pools entitle. It shares this
    /// table because it is the same kind of record — a raised notice an operator can read back — and
    /// keys its dedupe on the day plus the size of the overage rather than on a due date.
    /// </summary>
    LicenseCompliance = 4,
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
