using Modules.Assets.Data;
using Modules.Assets.Features.Discovery;

namespace Modules.Assets.Features.Drift;

/// <summary>
/// Compares what an operator asserted against what the network answered, one CI at a time.
/// <para>
/// The whole feature rests on WP-4.2's refusal to let a scan write a CI's own attributes: the CMDB
/// keeps saying what somebody typed and <c>assets.ci_discovery_facts</c> says what the device
/// reported, so the difference between them still exists to be found. Anything that "improved" the
/// intake by overwriting the recorded values would leave this comparator with two copies of one
/// number and nothing to report.
/// </para>
/// <para>
/// Pure: no database, no clock beyond the instant it is handed, no configuration. The whole matrix is
/// unit-tested.
/// </para>
/// </summary>
public static class DriftAnalyzer
{
    /// <summary>
    /// How long a CI may go unreported before the report says so, unless configuration or the caller
    /// says otherwise. A scan profile sweeps every five minutes, so a week of silence is a device that
    /// has genuinely stopped answering rather than one scan that missed it.
    /// </summary>
    public const int DefaultStaleAfterDays = 7;

    public static IReadOnlyList<DriftFinding> Analyse(
        DriftSubject subject,
        DateTimeOffset now,
        int staleAfterDays)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var observation = subject.Observation;
        var findings = new List<DriftFinding>(4);

        // Location. The one comparison the WP's own verification step exercises: an operator moves a CI
        // to another site while the device keeps answering with the sysLocation it was configured with.
        Compare(
            findings,
            DriftFields.Location,
            subject.SiteName,
            observation.SysLocation,
            observation.AnsweredSnmp,
            Text);

        // Hostname, for the types that record one. Compared short and lowercased, because a resolver
        // answers with a domain attached while a CI records the name somebody typed — WP-4.2's rule,
        // applied to the reverse direction.
        if (subject.RecordedHostname is not null)
        {
            Compare(
                findings,
                DriftFields.Hostname,
                subject.RecordedHostname,
                observation.SysName ?? observation.Hostname,
                observation.AnsweredSnmp,
                Host);
        }

        // Management IP, for network devices. A scan always found the device at some address, so the
        // Missing branch cannot fire here — the only outcomes are agreement, a disagreement worth
        // chasing, and a CI whose address nobody ever filled in.
        if (subject.RecordedManagementIp is not null)
        {
            Compare(
                findings,
                DriftFields.ManagementIp,
                subject.RecordedManagementIp,
                observation.Address,
                observation.AnsweredSnmp,
                Text);
        }

        // And the CI itself. This is what "missing" means to somebody reconciling an estate: the record
        // is still here and the thing it describes has stopped answering.
        if (staleAfterDays > 0 && now - observation.LastSeenAt > TimeSpan.FromDays(staleAfterDays))
        {
            findings.Add(new DriftFinding(
                DriftFields.LastSeen,
                DriftFindingKind.Missing,
                RecordedValue: null,
                ObservedValue: observation.LastSeenAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        }

        return findings;
    }

    /// <summary>
    /// The three-way comparison every field makes, with the one asymmetry that matters:
    /// <c>Missing</c> is only reported when the device answered SNMP.
    /// <para>
    /// Without that gate, every address that answers a ping and nothing else would report a missing
    /// location, a missing hostname and a missing everything — a report of hundreds of findings that
    /// all say "this device does not run an SNMP agent". A device that <em>did</em> answer and left the
    /// field empty is making a statement; one that never answered is silent, and silence is not drift.
    /// </para>
    /// </summary>
    private static void Compare(
        List<DriftFinding> findings,
        string field,
        string? recorded,
        string? observed,
        bool answeredSnmp,
        Func<string, string?> normalise)
    {
        var recordedValue = Trim(recorded);
        var observedValue = Trim(observed);

        if (recordedValue is null && observedValue is null)
        {
            return;
        }

        if (recordedValue is null)
        {
            findings.Add(new DriftFinding(field, DriftFindingKind.New, null, observedValue));
            return;
        }

        if (observedValue is null)
        {
            if (answeredSnmp)
            {
                findings.Add(new DriftFinding(field, DriftFindingKind.Missing, recordedValue, null));
            }

            return;
        }

        if (!string.Equals(normalise(recordedValue), normalise(observedValue), StringComparison.Ordinal))
        {
            findings.Add(new DriftFinding(field, DriftFindingKind.Changed, recordedValue, observedValue));
        }
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Case, surrounding space and runs of inner whitespace are not drift. "Primary  Data Centre" and
    /// "primary data centre" are one place typed by two people, and reporting them would bury the CI
    /// that genuinely moved building.
    /// </summary>
    private static string? Text(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>A hostname compares on its leftmost label, lowercased — the form WP-4.2 matches on.</summary>
    private static string? Host(string value) => DiscoveryIdentity.ShortHostname(value) ?? Text(value);

    /// <summary>
    /// Which of a CI's own attributes the comparator can read for a given type. TPH makes every one of
    /// them physically nullable, so this is the same "the schema lives above the database" rule
    /// <c>CiTypeSchema</c> exists for — and it is what keeps a switch from being told its hostname is
    /// missing when its type has no hostname to record.
    /// </summary>
    public static bool RecordsHostname(CiType type) => type is CiType.Server or CiType.Virtual;

    public static bool RecordsManagementIp(CiType type) => type is CiType.NetworkDevice;
}
