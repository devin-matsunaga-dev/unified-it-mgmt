using Modules.Assets.Data;

using Modules.Assets.Features.Cis;

namespace Modules.Assets.Seeding;

/// <summary>A supplier the seeded estate buys from. Keyed by a slug so contracts can name it in code.</summary>
public sealed record VendorSeed(
    string Key,
    string Name,
    string ContactName,
    string ContactEmail,
    string ContactPhone,
    string Website);

/// <summary>
/// A seeded agreement. Dates are expressed as offsets from the day the seeder runs, because the dev
/// database is recreated on most AppHost restarts and a hard-coded date would drift into the past.
/// </summary>
public sealed record ContractSeed(
    string Key,
    string VendorKey,
    string Number,
    string Name,
    ContractType Type,
    int StartDaysAgo,
    int EndInDays,
    bool AutoRenews,
    decimal Cost,
    string OwnerUsername,
    string? Notes = null);

/// <summary>
/// One CI of the seeded estate. <see cref="Attributes"/> holds the type's fixed attributes keyed
/// exactly as <see cref="Features.Cis.CiTypeSchema"/> names them, so the whole estate can be validated
/// against the schema without a database — see the unit tests. Ownership, coverage and age are optional
/// because most infrastructure has no personal holder and no purchase date.
/// </summary>
public sealed record CiSeed(
    string Key,
    CiType Type,
    string Name,
    string Description,
    IReadOnlyDictionary<string, string> Attributes,
    CiLifecycleState State)
{
    public string? AssetTag { get; init; }
    public string? SerialNumber { get; init; }

    /// <summary>Explicit site code. Left null on a held asset, which follows its owner's site.</summary>
    public string? SiteCode { get; init; }

    /// <summary>Explicit department code. Left null on a held asset, which follows its owner.</summary>
    public string? DepartmentCode { get; init; }

    public string? OwnerUsername { get; init; }

    /// <summary>Who held it before it was retired, so the check-in/out log reads as a real handback.</summary>
    public string? PreviousOwnerUsername { get; init; }

    public int? PurchasedDaysAgo { get; init; }

    /// <summary>Days from today the manufacturer warranty ends; negative is already expired.</summary>
    public int? WarrantyInDays { get; init; }

    public string? ContractKey { get; init; }

    /// <summary>How long the CI has been on the books. Drives the lifecycle history timestamps.</summary>
    public int AgeDays { get; init; } = 400;

    /// <summary>Values for the seeded user-defined fields, keyed by <see cref="CiCustomFieldSeed.Key"/>.</summary>
    public IReadOnlyDictionary<string, string> CustomFieldValues { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>A directed edge of the seeded dependency graph, naming its ends by <see cref="CiSeed.Key"/>.</summary>
public sealed record RelationshipSeed(
    string SourceKey,
    string TargetKey,
    CiRelationshipType Type,
    string Description);

public sealed record CiCustomFieldSeed(
    string Key,
    CiType CiType,
    string Label,
    CiCustomFieldType Type,
    IReadOnlyList<string> Options,
    int SortOrder);

/// <summary>
/// The estate the seeder writes: 60 CIs across all six types, arranged into three self-contained
/// dependency trees (one per site), plus the vendors, contracts and warranties that make the asset
/// screens look like a real organisation's rather than a fresh install's.
/// <para>
/// It is a hand-written table rather than generated data, so there is no randomness to reproduce:
/// re-running writes the same rows because every id is derived from an item's position in these
/// arrays. That makes the array order part of the contract — appending is safe, reordering is not.
/// </para>
/// <para>
/// Relationships read source→target as "the source depends on the target" (WP-2.3), so every tree's
/// root is its site's router: <c>impacted-by(router)</c> returns the whole site, and
/// <c>ancestors(service)</c> walks down to the hardware it ultimately needs. The three trees are
/// deliberately not joined by WAN links — a real estate would have them, but keeping the sites apart
/// makes each root's blast radius a bounded answer somebody can read.
/// </para>
/// </summary>
public static class AssetsEstate
{
    public static readonly IReadOnlyList<VendorSeed> Vendors =
    [
        new("dell", "Dell Technologies", "Aisha Rahman", "aisha.rahman@dell.example", "+44 20 7946 0101",
            "https://dell.example"),
        new("cisco", "Cisco Systems", "Marco Bianchi", "marco.bianchi@cisco.example", "+44 20 7946 0102",
            "https://cisco.example"),
        new("hpe", "Hewlett Packard Enterprise", "Sofia Lindqvist", "sofia.lindqvist@hpe.example",
            "+44 20 7946 0103", "https://hpe.example"),
        new("microsoft", "Microsoft", "Daniel Okafor", "daniel.okafor@microsoft.example", "+44 20 7946 0104",
            "https://microsoft.example"),
        new("northwind", "Northwind Software", "Priya Nair", "priya.nair@northwind.example", "+44 20 7946 0105",
            "https://northwind.example"),
        new("contoso", "Contoso Managed Services", "Tomas Novak", "tomas.novak@contoso.example",
            "+44 20 7946 0106", "https://contoso.example"),
    ];

    /// <summary>
    /// Six agreements spanning every status the contract page can show: comfortably active, expiring
    /// inside the 30-day and 7-day notice windows, and one already expired. The renewal job therefore
    /// has something to find on its first pass in a freshly seeded database.
    /// </summary>
    public static readonly IReadOnlyList<ContractSeed> Contracts =
    [
        new("smartnet", "cisco", "CN-2026-0001", "SmartNet core network support", ContractType.Support,
            StartDaysAgo: 540, EndInDays: 240, AutoRenews: true, Cost: 24_500m, OwnerUsername: "manager1",
            Notes: "24x7x4 hardware replacement on the core and branch routing estate."),
        new("prosupport", "dell", "CN-2026-0002", "Server hardware ProSupport", ContractType.Support,
            StartDaysAgo: 700, EndInDays: 21, AutoRenews: false, Cost: 18_750m, OwnerUsername: "manager4",
            Notes: "Next business day parts and labour. Renewal quote requested from the account manager."),
        new("campus-switching", "hpe", "CN-2026-0003", "Campus switching maintenance", ContractType.Maintenance,
            StartDaysAgo: 300, EndInDays: 65, AutoRenews: true, Cost: 9_600m, OwnerUsername: "manager1"),
        new("enterprise-agreement", "microsoft", "CN-2026-0004", "Enterprise agreement", ContractType.Subscription,
            StartDaysAgo: 330, EndInDays: 400, AutoRenews: true, Cost: 64_000m, OwnerUsername: "manager4"),
        new("erp-support", "northwind", "CN-2026-0005", "Finance ERP support and updates", ContractType.Support,
            StartDaysAgo: 725, EndInDays: 5, AutoRenews: false, Cost: 31_000m, OwnerUsername: "manager2",
            Notes: "Renewal must be signed off by Finance before the year end close."),
        new("managed-backup", "contoso", "CN-2026-0006", "Managed backup service", ContractType.Support,
            StartDaysAgo: 750, EndInDays: -20, AutoRenews: false, Cost: 7_200m, OwnerUsername: "manager4",
            Notes: "Lapsed while the replacement service was being tendered."),
    ];

    /// <summary>
    /// Two optional user-defined fields, so a fresh database demonstrates the custom-field surface the
    /// CI form renders. Both are optional on purpose: a required one would make every CI created by
    /// hand — and every existing integration test that posts one — fail validation.
    /// </summary>
    public static readonly IReadOnlyList<CiCustomFieldSeed> CustomFields =
    [
        // The runtime stand-in for a hardware subtype: CiType stops at "Hardware", and this is how a
        // laptop says it is not a printer. Seeded so a fresh estate can be filtered on it immediately.
        new("hardware_type", CiType.Hardware, "Hardware type", CiCustomFieldType.Select,
            ["Laptop", "Desktop", "Printer", "Monitor", "Other"], 0),
        new("purchase_order", CiType.Hardware, "Purchase order", CiCustomFieldType.Text, [], 1),
        new("backup_schedule", CiType.Server, "Backup schedule", CiCustomFieldType.Select,
            ["Nightly", "Weekly", "None"], 1),
    ];

    /// <summary>
    /// The 60 CIs. Grouped by type in the order the estate reads: the network first because every tree
    /// hangs off it, then the machines, then what runs on them, then the services, then user hardware.
    /// </summary>
    public static readonly IReadOnlyList<CiSeed> Cis =
    [
        // ---- Network (10) -------------------------------------------------------------------
        Network("dc1-core-rtr-01", "DC1 core router", "Primary Data Centre edge router. Root of the data centre dependency tree.",
                "10.10.0.1", "Cisco", 24, NetworkDeviceRoles.Edge) with
            { SiteCode = "DC1", OwnerUsername = "technician2", AssetTag = "NET-0001",
              SerialNumber = "FTX2401R001", ContractKey = "smartnet",
              PurchasedDaysAgo = 1_100, WarrantyInDays = 640, AgeDays = 1_100 },
        Network("dc1-core-sw-01", "DC1 core switch A", "First of the redundant core switch pair.",
                "10.10.0.2", "Cisco", 48, NetworkDeviceRoles.Core) with
            { SiteCode = "DC1", OwnerUsername = "technician2", AssetTag = "NET-0002",
              SerialNumber = "FTX2401S001", ContractKey = "smartnet",
              PurchasedDaysAgo = 1_100, WarrantyInDays = 640, AgeDays = 1_100 },
        Network("dc1-core-sw-02", "DC1 core switch B", "Second of the redundant core switch pair.",
                "10.10.0.3", "Cisco", 48, NetworkDeviceRoles.Core) with
            { SiteCode = "DC1", OwnerUsername = "technician2", AssetTag = "NET-0003",
              SerialNumber = "FTX2401S002", ContractKey = "smartnet",
              PurchasedDaysAgo = 1_100, WarrantyInDays = 640, AgeDays = 1_100 },
        Network("dc1-acc-sw-01", "DC1 access switch", "Out-of-band and backup network access switch.",
                "10.10.0.4", "Cisco", 24, NetworkDeviceRoles.Access) with
            // Held by technician2 for the same reason WP-3.7 gave the three core CIs above an owner:
            // WP-3.12 makes this the down-able device, so its ticket is the one the Phase 3 demo puts
            // on screen — and a demo whose asset card reads "nobody holds this asset" proves the
            // wiring and nothing else. Found by this package's own hand-verification.
            { SiteCode = "DC1", OwnerUsername = "technician2", AssetTag = "NET-0004",
              SerialNumber = "FTX2401S003", ContractKey = "smartnet",
              PurchasedDaysAgo = 800, WarrantyInDays = 460, AgeDays = 800 },
        Network("hq-edge-rtr-01", "HQ edge router", "Head Office internet edge. Root of the Head Office dependency tree.",
                "10.20.0.1", "Cisco", 16, NetworkDeviceRoles.Edge) with
            { SiteCode = "HQ", AssetTag = "NET-0005", SerialNumber = "FTX2402R001", ContractKey = "smartnet",
              PurchasedDaysAgo = 900, WarrantyInDays = 560, AgeDays = 900 },
        Network("hq-acc-sw-01", "HQ floor 1 switch", "Head Office ground floor access switch.",
                "10.20.0.2", "Aruba", 48, NetworkDeviceRoles.Access) with
            { SiteCode = "HQ", AssetTag = "NET-0006", SerialNumber = "CN2402S001", ContractKey = "campus-switching",
              PurchasedDaysAgo = 700, WarrantyInDays = 400, AgeDays = 700 },
        Network("hq-acc-sw-02", "HQ floor 2 switch", "Head Office first floor access switch.",
                "10.20.0.3", "Aruba", 48, NetworkDeviceRoles.Access) with
            { SiteCode = "HQ", AssetTag = "NET-0007", SerialNumber = "CN2402S002", ContractKey = "campus-switching",
              PurchasedDaysAgo = 700, WarrantyInDays = 400, AgeDays = 700 },
        Network("hq-acc-sw-03", "HQ floor 3 switch", "Head Office second floor access switch. Warranty is close to expiry.",
                "10.20.0.4", "Aruba", 48, NetworkDeviceRoles.Access) with
            { SiteCode = "HQ", AssetTag = "NET-0008", SerialNumber = "CN2402S003", ContractKey = "campus-switching",
              PurchasedDaysAgo = 1_080, WarrantyInDays = 12, AgeDays = 1_080 },
        Network("br1-rtr-01", "Branch router", "Regional Branch router. Root of the branch dependency tree.",
                "10.30.0.1", "Cisco", 8, NetworkDeviceRoles.Edge) with
            { SiteCode = "BR1", AssetTag = "NET-0009", SerialNumber = "FTX2403R001", ContractKey = "smartnet",
              PurchasedDaysAgo = 620, WarrantyInDays = 470, AgeDays = 620 },
        Network("br1-sw-01", "Branch switch", "Regional Branch access switch.",
                "10.30.0.2", "Cisco", 24, NetworkDeviceRoles.Access) with
            { SiteCode = "BR1", AssetTag = "NET-0010", SerialNumber = "FTX2403S001", ContractKey = "smartnet",
              PurchasedDaysAgo = 620, WarrantyInDays = 470, AgeDays = 620 },

        // ---- Servers (10) -------------------------------------------------------------------
        Server("dc1-esx-01", "DC1 hypervisor host 1", "Virtualisation host carrying the finance and portal front ends.",
               "dc1-esx-01.corp.local", "VMware ESXi 8.0", 32, 512) with
            { SiteCode = "DC1", AssetTag = "SRV-0001", SerialNumber = "DL380-0001", ContractKey = "prosupport",
              PurchasedDaysAgo = 730, WarrantyInDays = 365, AgeDays = 730,
              CustomFieldValues = new Dictionary<string, string> { ["backup_schedule"] = "Nightly" } },
        Server("dc1-esx-02", "DC1 hypervisor host 2", "Virtualisation host carrying the finance batch and database tiers.",
               "dc1-esx-02.corp.local", "VMware ESXi 8.0", 32, 512) with
            { SiteCode = "DC1", AssetTag = "SRV-0002", SerialNumber = "DL380-0002", ContractKey = "prosupport",
              PurchasedDaysAgo = 730, WarrantyInDays = 365, AgeDays = 730,
              CustomFieldValues = new Dictionary<string, string> { ["backup_schedule"] = "Nightly" } },
        Server("dc1-esx-03", "DC1 hypervisor host 3", "Virtualisation host carrying mail, archive and the second web front end.",
               "dc1-esx-03.corp.local", "VMware ESXi 8.0", 32, 512) with
            { SiteCode = "DC1", AssetTag = "SRV-0003", SerialNumber = "DL380-0003", ContractKey = "prosupport",
              PurchasedDaysAgo = 730, WarrantyInDays = 365, AgeDays = 730,
              CustomFieldValues = new Dictionary<string, string> { ["backup_schedule"] = "Nightly" } },
        Server("dc1-esx-04", "DC1 hypervisor host 4", "Virtualisation host carrying payroll data and monitoring. Warranty expires within days.",
               "dc1-esx-04.corp.local", "VMware ESXi 8.0", 24, 384) with
            { SiteCode = "DC1", AssetTag = "SRV-0004", SerialNumber = "DL380-0004", ContractKey = "prosupport",
              PurchasedDaysAgo = 1_090, WarrantyInDays = 3, AgeDays = 1_090,
              CustomFieldValues = new Dictionary<string, string> { ["backup_schedule"] = "Weekly" } },
        Server("dc1-db-01", "DC1 database server", "Physical database server for the finance reporting warehouse.",
               "dc1-db-01.corp.local", "Ubuntu 24.04 LTS", 16, 256) with
            { SiteCode = "DC1", AssetTag = "SRV-0005", SerialNumber = "R650-0001", ContractKey = "prosupport",
              PurchasedDaysAgo = 540, WarrantyInDays = 555, AgeDays = 540,
              CustomFieldValues = new Dictionary<string, string> { ["backup_schedule"] = "Nightly" } },
        Server("dc1-bkp-01", "DC1 backup server", "Backup target for the data centre estate. Out of warranty.",
               "dc1-bkp-01.corp.local", "Ubuntu 24.04 LTS", 8, 128) with
            { SiteCode = "DC1", AssetTag = "SRV-0006", SerialNumber = "R650-0002", ContractKey = "managed-backup",
              PurchasedDaysAgo = 1_500, WarrantyInDays = -40, AgeDays = 1_500,
              CustomFieldValues = new Dictionary<string, string> { ["backup_schedule"] = "None" } },
        Server("hq-fs-01", "HQ file server", "Head Office departmental file shares.",
               "hq-fs-01.corp.local", "Windows Server 2025", 8, 64) with
            { SiteCode = "HQ", AssetTag = "SRV-0007", SerialNumber = "R450-0001", ContractKey = "prosupport",
              PurchasedDaysAgo = 620, WarrantyInDays = 475, AgeDays = 620,
              CustomFieldValues = new Dictionary<string, string> { ["backup_schedule"] = "Nightly" } },
        Server("hq-dc-01", "HQ domain controller", "Head Office directory and authentication.",
               "hq-dc-01.corp.local", "Windows Server 2025", 4, 32) with
            { SiteCode = "HQ", AssetTag = "SRV-0008", SerialNumber = "R450-0002", ContractKey = "prosupport",
              PurchasedDaysAgo = 620, WarrantyInDays = 475, AgeDays = 620,
              CustomFieldValues = new Dictionary<string, string> { ["backup_schedule"] = "Nightly" } },
        Server("hq-print-01", "HQ print server", "Head Office print queues.",
               "hq-print-01.corp.local", "Windows Server 2025", 4, 16) with
            { SiteCode = "HQ", AssetTag = "SRV-0009", SerialNumber = "R450-0003", ContractKey = "prosupport",
              PurchasedDaysAgo = 620, WarrantyInDays = 475, AgeDays = 620,
              CustomFieldValues = new Dictionary<string, string> { ["backup_schedule"] = "Weekly" } },
        Server("br1-srv-01", "Branch server", "Regional Branch host running the point of sale server.",
               "br1-srv-01.corp.local", "Windows Server 2025", 8, 64) with
            { SiteCode = "BR1", AssetTag = "SRV-0010", SerialNumber = "R350-0001", ContractKey = "prosupport",
              PurchasedDaysAgo = 500, WarrantyInDays = 595, AgeDays = 500,
              CustomFieldValues = new Dictionary<string, string> { ["backup_schedule"] = "Weekly" } },

        // ---- Virtual machines (12) ----------------------------------------------------------
        // Virtual machines carry no asset tag or serial: nothing was ever bought or stickered, which
        // is also what keeps the filtered unique indexes on those columns meaningful.
        Virtual("dc1-vm-app-01", "Finance ERP application server", "Application tier of the finance ERP.",
                "dc1-vm-app-01.corp.local", "VMware ESXi 8.0", 8, 32) with { SiteCode = "DC1", AgeDays = 700 },
        Virtual("dc1-vm-app-02", "Finance ERP batch server", "Overnight batch and reporting tier of the finance ERP.",
                "dc1-vm-app-02.corp.local", "VMware ESXi 8.0", 8, 32) with { SiteCode = "DC1", AgeDays = 700 },
        Virtual("dc1-vm-web-01", "Customer portal web front end 1", "First customer portal web front end.",
                "dc1-vm-web-01.corp.local", "VMware ESXi 8.0", 4, 16) with { SiteCode = "DC1", AgeDays = 520 },
        Virtual("dc1-vm-web-02", "Customer portal web front end 2", "Second customer portal web front end.",
                "dc1-vm-web-02.corp.local", "VMware ESXi 8.0", 4, 16) with { SiteCode = "DC1", AgeDays = 520 },
        Virtual("dc1-vm-sql-01", "Finance database server", "Database instance behind the finance ERP and reporting.",
                "dc1-vm-sql-01.corp.local", "VMware ESXi 8.0", 16, 128) with { SiteCode = "DC1", AgeDays = 700 },
        Virtual("dc1-vm-sql-02", "Payroll database server", "Database instance behind payroll.",
                "dc1-vm-sql-02.corp.local", "VMware ESXi 8.0", 8, 64) with { SiteCode = "DC1", AgeDays = 610 },
        Virtual("dc1-vm-mail-01", "Mail gateway", "Inbound and outbound mail relay.",
                "dc1-vm-mail-01.corp.local", "VMware ESXi 8.0", 4, 16) with { SiteCode = "DC1", AgeDays = 610 },
        Virtual("dc1-vm-mon-01", "Monitoring server", "Collects availability and performance data for the estate.",
                "dc1-vm-mon-01.corp.local", "VMware ESXi 8.0", 4, 16) with { SiteCode = "DC1", AgeDays = 430 },
        Virtual("dc1-vm-dc-02", "Secondary domain controller", "Data centre replica of the corporate directory.",
                "dc1-vm-dc-02.corp.local", "VMware ESXi 8.0", 4, 8) with { SiteCode = "DC1", AgeDays = 700 },
        Virtual("dc1-vm-hr-01", "Payroll application server", "Application tier of the payroll suite.",
                "dc1-vm-hr-01.corp.local", "VMware ESXi 8.0", 8, 32) with { SiteCode = "DC1", AgeDays = 610 },
        Virtual("dc1-vm-file-01", "Archive file server", "Long term archive shares.",
                "dc1-vm-file-01.corp.local", "VMware ESXi 8.0", 4, 32) with { SiteCode = "DC1", AgeDays = 430 },
        Virtual("br1-vm-pos-01", "Branch point of sale server", "Point of sale server for the branch tills.",
                "br1-vm-pos-01.corp.local", "Microsoft Hyper-V", 4, 16) with { SiteCode = "BR1", AgeDays = 480 },

        // ---- Software (8) -------------------------------------------------------------------
        Software("sw-erp", "Finance ERP", "Ledger, purchasing and reporting application.",
                 "Northwind Software", "2026.1") with
            { SiteCode = "DC1", ContractKey = "erp-support", AgeDays = 700 },
        Software("sw-payroll", "Payroll Suite", "Payroll calculation and payslip distribution.",
                 "Northwind Software", "12.4") with
            { SiteCode = "DC1", ContractKey = "erp-support", AgeDays = 610 },
        Software("sw-portal", "Customer Portal", "Public self-service portal for customers.",
                 "Contoso Digital", "3.2") with { SiteCode = "DC1", AgeDays = 520 },
        Software("sw-sql-fin", "SQL Server (finance)", "Database engine shared by finance reporting and the customer portal.",
                 "Microsoft", "2025") with
            { SiteCode = "DC1", ContractKey = "enterprise-agreement", AgeDays = 700 },
        Software("sw-pg-payroll", "PostgreSQL (payroll)", "Database engine behind the payroll suite.",
                 "PostgreSQL Global Development Group", "18.1") with { SiteCode = "DC1", AgeDays = 610 },
        Software("sw-mailgw", "Mail Gateway", "Mail relay, filtering and archiving.",
                 "Contoso Digital", "9.0") with { SiteCode = "DC1", AgeDays = 610 },
        Software("sw-monitor", "Monitoring Suite", "Availability and performance monitoring.",
                 "Contoso Digital", "7.1") with { SiteCode = "DC1", AgeDays = 430 },
        Software("sw-pos", "Branch POS Application", "Till and stock application for the branch.",
                 "Contoso Digital", "5.6") with { SiteCode = "BR1", AgeDays = 480 },

        // ---- Business services (6) ----------------------------------------------------------
        Logical("svc-finance", "Finance Reporting Service", "Month end and statutory reporting for the finance team.",
                "Finance reporting and month end close", "Gold") with
            { SiteCode = "DC1", DepartmentCode = "FIN", AgeDays = 700 },
        Logical("svc-payroll", "Payroll Service", "Monthly payroll run and payslip delivery.",
                "Monthly payroll processing", "Gold") with
            { SiteCode = "DC1", DepartmentCode = "HR", AgeDays = 610 },
        Logical("svc-portal", "Customer Portal Service", "Customer facing self-service, shares its database with finance reporting.",
                "Customer self-service", "Platinum") with
            { SiteCode = "DC1", DepartmentCode = "OPS", AgeDays = 520 },
        Logical("svc-mail", "Corporate Email Service", "Inbound and outbound corporate email.",
                "Corporate email", "Gold") with
            { SiteCode = "DC1", DepartmentCode = "IT", AgeDays = 610 },
        Logical("svc-pos", "Branch Point of Sale Service", "Tills and stock lookups at the regional branch.",
                "Retail point of sale", "Silver") with
            { SiteCode = "BR1", DepartmentCode = "OPS", AgeDays = 480 },
        Logical("svc-fileprint", "Corporate File and Print Service", "Head Office file shares and print queues.",
                "File shares and printing", "Bronze") with
            { SiteCode = "HQ", DepartmentCode = "IT", AgeDays = 620 },

        // ---- User hardware (14) -------------------------------------------------------------
        // Site and department are left unset on held assets: they follow the owner's profile, exactly
        // as the WP-2.2 assignment drawer prefills them.
        Laptop("hw-lt-01", "Latitude 7450", "LT-0001", "5CG4101001", "enduser1", 620, 400, "PO-2024-0141"),
        Laptop("hw-lt-02", "Latitude 7450", "LT-0002", "5CG4101002", "enduser2", 620, 250, "PO-2024-0141"),
        Laptop("hw-lt-03", "Latitude 7450", "LT-0003", "5CG4101003", "enduser3", 1_070, 25, "PO-2023-0088"),
        Laptop("hw-lt-04", "Latitude 5550", "LT-0004", "5CG4101004", "enduser4", 1_160, -60, "PO-2023-0088"),
        Laptop("hw-lt-05", "Latitude 7450", "LT-0005", "5CG4101005", "enduser5", 800, 180, "PO-2024-0207"),
        Laptop("hw-lt-06", "Latitude 5550", "LT-0006", "5CG4101006", "enduser6", 1_090, 6, "PO-2023-0088"),
        Laptop("hw-lt-07", "Latitude 5550", "LT-0007", "5CG4101007", "enduser7", 1_110, -10, "PO-2023-0088") with
            { State = CiLifecycleState.InRepair,
              Description = "Standard build laptop. Currently with the supplier for a keyboard replacement." },
        Laptop("hw-lt-08", "Latitude 7450", "LT-0008", "5CG4101008", "enduser8", 500, 590, "PO-2025-0012"),
        Laptop("hw-lt-09", "Latitude 7450", "LT-0009", "5CG4101009", owner: null, 120, 975, "PO-2026-0033") with
            { State = CiLifecycleState.InStock, SiteCode = "HQ", DepartmentCode = "IT", AgeDays = 120,
              Description = "Spare standard build laptop held for a starter or a swap out." },
        Laptop("hw-lt-10", "Latitude 7450", "LT-0010", "5CG4101010", owner: null, purchasedDaysAgo: null,
               warrantyInDays: null, purchaseOrder: "PO-2026-0061") with
            { State = CiLifecycleState.Ordered, SiteCode = "HQ", DepartmentCode = "IT", AgeDays = 9,
              Description = "Ordered for the operations starter, not yet received." },

        Hardware("hw-ws-01", "Workstation — Manager One", "Desk workstation.", "Dell", "OptiPlex 7020") with
            { AssetTag = "WS-0001", SerialNumber = "5CG4102001", OwnerUsername = "manager1",
              PurchasedDaysAgo = 450, WarrantyInDays = 640, AgeDays = 450,
              CustomFieldValues = new Dictionary<string, string>
                { ["hardware_type"] = "Desktop", ["purchase_order"] = "PO-2025-0004" } },
        Hardware("hw-ws-02", "Workstation — retired", "Desk workstation withdrawn from service after a hardware fault.",
                 "Dell", "OptiPlex 5090") with
            { State = CiLifecycleState.Retired, AssetTag = "WS-0002", SerialNumber = "5CG4102002",
              PreviousOwnerUsername = "manager2", SiteCode = "BR1", DepartmentCode = "FIN",
              PurchasedDaysAgo = 1_650, WarrantyInDays = -190, AgeDays = 1_650,
              CustomFieldValues = new Dictionary<string, string>
                { ["hardware_type"] = "Desktop", ["purchase_order"] = "PO-2022-0311" } },
        Hardware("hw-pr-01", "HQ floor 1 printer", "Shared multifunction printer by the ground floor kitchen.",
                 "HP", "LaserJet E60155") with
            { AssetTag = "PR-0001", SerialNumber = "CNB4103001", SiteCode = "HQ", DepartmentCode = "OPS",
              PurchasedDaysAgo = 610, WarrantyInDays = 150, AgeDays = 610,
              CustomFieldValues = new Dictionary<string, string> { ["hardware_type"] = "Printer" } },
        Hardware("hw-pr-02", "Branch printer — disposed", "Branch multifunction printer, scrapped after the paper feed failed.",
                 "HP", "LaserJet E50145") with
            { State = CiLifecycleState.Disposed, AssetTag = "PR-0002", SerialNumber = "CNB4103002",
              SiteCode = "BR1", DepartmentCode = "OPS",
              PurchasedDaysAgo = 1_800, WarrantyInDays = -350, AgeDays = 1_800,
              CustomFieldValues = new Dictionary<string, string> { ["hardware_type"] = "Printer" } },
    ];

    /// <summary>
    /// The dependency graph: 60 edges forming one tree per site. Each reads "source depends on target",
    /// so a switch <c>ConnectsTo</c> its router and a service <c>DependsOn</c> the software it is made of.
    /// </summary>
    public static readonly IReadOnlyList<RelationshipSeed> Relationships =
    [
        // Primary Data Centre: router → core switches → hosts → VMs → software → services.
        Edge("dc1-core-sw-01", "dc1-core-rtr-01", CiRelationshipType.ConnectsTo, "Core uplink to the edge router."),
        Edge("dc1-core-sw-02", "dc1-core-rtr-01", CiRelationshipType.ConnectsTo, "Core uplink to the edge router."),
        Edge("dc1-acc-sw-01", "dc1-core-sw-01", CiRelationshipType.ConnectsTo, "Access layer uplink."),
        Edge("dc1-esx-01", "dc1-core-sw-01", CiRelationshipType.ConnectsTo, "Host networking."),
        Edge("dc1-esx-02", "dc1-core-sw-01", CiRelationshipType.ConnectsTo, "Host networking."),
        Edge("dc1-esx-03", "dc1-core-sw-02", CiRelationshipType.ConnectsTo, "Host networking."),
        Edge("dc1-esx-04", "dc1-core-sw-02", CiRelationshipType.ConnectsTo, "Host networking."),
        Edge("dc1-db-01", "dc1-core-sw-02", CiRelationshipType.ConnectsTo, "Database server networking."),
        Edge("dc1-bkp-01", "dc1-acc-sw-01", CiRelationshipType.ConnectsTo, "Backup network."),
        Edge("dc1-vm-app-01", "dc1-esx-01", CiRelationshipType.RunsOn, "Virtual machine placement."),
        Edge("dc1-vm-web-01", "dc1-esx-01", CiRelationshipType.RunsOn, "Virtual machine placement."),
        Edge("dc1-vm-dc-02", "dc1-esx-01", CiRelationshipType.RunsOn, "Virtual machine placement."),
        Edge("dc1-vm-app-02", "dc1-esx-02", CiRelationshipType.RunsOn, "Virtual machine placement."),
        Edge("dc1-vm-sql-01", "dc1-esx-02", CiRelationshipType.RunsOn, "Virtual machine placement."),
        Edge("dc1-vm-hr-01", "dc1-esx-02", CiRelationshipType.RunsOn, "Virtual machine placement."),
        Edge("dc1-vm-web-02", "dc1-esx-03", CiRelationshipType.RunsOn, "Virtual machine placement."),
        Edge("dc1-vm-mail-01", "dc1-esx-03", CiRelationshipType.RunsOn, "Virtual machine placement."),
        Edge("dc1-vm-file-01", "dc1-esx-03", CiRelationshipType.RunsOn, "Virtual machine placement."),
        Edge("dc1-vm-sql-02", "dc1-esx-04", CiRelationshipType.RunsOn, "Virtual machine placement."),
        Edge("dc1-vm-mon-01", "dc1-esx-04", CiRelationshipType.RunsOn, "Virtual machine placement."),
        Edge("sw-erp", "dc1-vm-app-01", CiRelationshipType.HostedOn, "Application tier."),
        Edge("sw-erp", "dc1-vm-app-02", CiRelationshipType.HostedOn, "Overnight batch tier."),
        Edge("sw-payroll", "dc1-vm-hr-01", CiRelationshipType.HostedOn, "Application tier."),
        Edge("sw-portal", "dc1-vm-web-01", CiRelationshipType.HostedOn, "First web front end."),
        Edge("sw-portal", "dc1-vm-web-02", CiRelationshipType.HostedOn, "Second web front end."),
        Edge("sw-sql-fin", "dc1-vm-sql-01", CiRelationshipType.HostedOn, "Database instance."),
        Edge("sw-pg-payroll", "dc1-vm-sql-02", CiRelationshipType.HostedOn, "Database instance."),
        Edge("sw-mailgw", "dc1-vm-mail-01", CiRelationshipType.HostedOn, "Mail relay."),
        Edge("sw-monitor", "dc1-vm-mon-01", CiRelationshipType.HostedOn, "Monitoring server."),
        Edge("svc-finance", "sw-erp", CiRelationshipType.DependsOn, "Reporting is produced by the ERP."),
        Edge("svc-finance", "sw-sql-fin", CiRelationshipType.DependsOn, "Reporting reads the finance database."),
        Edge("svc-finance", "dc1-db-01", CiRelationshipType.DependsOn, "Month end extracts land on the reporting warehouse."),
        Edge("svc-payroll", "sw-payroll", CiRelationshipType.DependsOn, "Payroll is calculated by the suite."),
        Edge("svc-payroll", "sw-pg-payroll", CiRelationshipType.DependsOn, "Payroll data lives in this database."),
        Edge("svc-portal", "sw-portal", CiRelationshipType.DependsOn, "The portal application serves customers."),
        // The shared database is what makes a blast radius interesting: one instance, two services.
        Edge("svc-portal", "sw-sql-fin", CiRelationshipType.DependsOn, "The portal shares the finance database instance."),
        Edge("svc-mail", "sw-mailgw", CiRelationshipType.DependsOn, "Mail flows through the gateway."),
        Edge("hw-lt-04", "dc1-acc-sw-01", CiRelationshipType.ConnectsTo, "Desk network port."),
        Edge("hw-lt-08", "dc1-acc-sw-01", CiRelationshipType.ConnectsTo, "Desk network port."),

        // Head Office: router → access switches → servers → file and print service.
        Edge("hq-acc-sw-01", "hq-edge-rtr-01", CiRelationshipType.ConnectsTo, "Access layer uplink."),
        Edge("hq-acc-sw-02", "hq-edge-rtr-01", CiRelationshipType.ConnectsTo, "Access layer uplink."),
        Edge("hq-acc-sw-03", "hq-edge-rtr-01", CiRelationshipType.ConnectsTo, "Access layer uplink."),
        Edge("hq-fs-01", "hq-acc-sw-01", CiRelationshipType.ConnectsTo, "Server networking."),
        Edge("hq-dc-01", "hq-acc-sw-01", CiRelationshipType.ConnectsTo, "Server networking."),
        Edge("hq-print-01", "hq-acc-sw-02", CiRelationshipType.ConnectsTo, "Server networking."),
        Edge("hq-fs-01", "hq-dc-01", CiRelationshipType.DependsOn, "Share permissions are resolved against the directory."),
        Edge("hq-print-01", "hq-dc-01", CiRelationshipType.DependsOn, "Print queues authenticate against the directory."),
        Edge("svc-fileprint", "hq-fs-01", CiRelationshipType.DependsOn, "File shares."),
        Edge("svc-fileprint", "hq-print-01", CiRelationshipType.DependsOn, "Print queues."),
        Edge("hw-lt-01", "hq-acc-sw-03", CiRelationshipType.ConnectsTo, "Desk network port."),
        Edge("hw-lt-02", "hq-acc-sw-03", CiRelationshipType.ConnectsTo, "Desk network port."),
        Edge("hw-lt-05", "hq-acc-sw-03", CiRelationshipType.ConnectsTo, "Desk network port."),
        Edge("hw-ws-01", "hq-acc-sw-02", CiRelationshipType.ConnectsTo, "Desk network port."),
        Edge("hw-pr-01", "hq-acc-sw-02", CiRelationshipType.ConnectsTo, "Printer network port."),

        // Regional Branch: router → switch → server → VM → application → service.
        Edge("br1-sw-01", "br1-rtr-01", CiRelationshipType.ConnectsTo, "Branch uplink."),
        Edge("br1-srv-01", "br1-sw-01", CiRelationshipType.ConnectsTo, "Server networking."),
        Edge("br1-vm-pos-01", "br1-srv-01", CiRelationshipType.RunsOn, "Virtual machine placement."),
        Edge("sw-pos", "br1-vm-pos-01", CiRelationshipType.HostedOn, "Point of sale application."),
        Edge("svc-pos", "sw-pos", CiRelationshipType.DependsOn, "The tills run against this application."),
        Edge("hw-lt-03", "br1-sw-01", CiRelationshipType.ConnectsTo, "Desk network port."),
        Edge("hw-lt-06", "br1-sw-01", CiRelationshipType.ConnectsTo, "Desk network port."),
    ];

    /// <param name="role">
    /// One of <see cref="NetworkDeviceRoles"/>. Seeded rather than left unset so a fresh estate draws
    /// the hierarchy the topology's Network view is built on, instead of one flat rank of devices.
    /// </param>
    private static CiSeed Network(
        string key, string name, string description, string ip, string vendor, int ports, string role) =>
        new(key, CiType.NetworkDevice, name, description, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["managementIp"] = ip,
            ["vendor"] = vendor,
            ["portCount"] = ports.ToString(),
            ["role"] = role,
        }, CiLifecycleState.Deployed) { DepartmentCode = "IT" };

    private static CiSeed Server(
        string key, string name, string description, string hostname, string operatingSystem, int cpuCores, int ramGb) =>
        new(key, CiType.Server, name, description, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hostname"] = hostname,
            ["operatingSystem"] = operatingSystem,
            ["cpuCores"] = cpuCores.ToString(),
            ["ramGb"] = ramGb.ToString(),
        }, CiLifecycleState.Deployed) { DepartmentCode = "IT" };

    private static CiSeed Virtual(
        string key, string name, string description, string hostname, string hypervisor, int vcpuCores, int ramGb) =>
        new(key, CiType.Virtual, name, description, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hostname"] = hostname,
            ["hypervisor"] = hypervisor,
            ["vcpuCores"] = vcpuCores.ToString(),
            ["ramGb"] = ramGb.ToString(),
        }, CiLifecycleState.Deployed) { DepartmentCode = "IT" };

    private static CiSeed Software(string key, string name, string description, string vendor, string version) =>
        new(key, CiType.Software, name, description, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vendor"] = vendor,
            ["version"] = version,
        }, CiLifecycleState.Deployed) { DepartmentCode = "IT" };

    private static CiSeed Logical(string key, string name, string description, string purpose, string serviceTier) =>
        new(key, CiType.Logical, name, description, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["purpose"] = purpose,
            ["serviceTier"] = serviceTier,
        }, CiLifecycleState.Deployed);

    private static CiSeed Hardware(string key, string name, string description, string manufacturer, string model) =>
        new(key, CiType.Hardware, name, description, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["manufacturer"] = manufacturer,
            ["model"] = model,
        }, CiLifecycleState.Deployed);

    private static CiSeed Laptop(
        string key,
        string model,
        string assetTag,
        string serialNumber,
        string? owner,
        int? purchasedDaysAgo,
        int? warrantyInDays,
        string purchaseOrder) =>
        Hardware(key, $"Laptop {assetTag}", "Standard build laptop.", "Dell", model) with
        {
            AssetTag = assetTag,
            SerialNumber = serialNumber,
            OwnerUsername = owner,
            PurchasedDaysAgo = purchasedDaysAgo,
            WarrantyInDays = warrantyInDays,
            AgeDays = purchasedDaysAgo ?? 30,
            CustomFieldValues = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hardware_type"] = "Laptop",
                ["purchase_order"] = purchaseOrder,
            },
        };

    private static RelationshipSeed Edge(string source, string target, CiRelationshipType type, string description) =>
        new(source, target, type, description);
}
