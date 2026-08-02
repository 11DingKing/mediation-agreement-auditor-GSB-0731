using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MediationAudit;

/// <summary>
/// Severity of an <see cref="AuditIssue"/>. Ordered from least to most serious so that
/// callers can compare severities numerically without depending on the display text.
/// </summary>
public enum Severity
{
    /// <summary>Informational finding that does not by itself invalidate the package.</summary>
    Info = 0,

    /// <summary>A finding that is suspicious and should be reviewed by a mediator.</summary>
    Warning = 1,

    /// <summary>A finding that makes the agreement package internally inconsistent.</summary>
    Error = 2,
}

/// <summary>
/// Identifies which top-level JSON array (or the document root) an evidence path is rooted in.
/// Used to build stable JSON evidence paths and to give issues a traversal-independent sort order.
/// </summary>
public enum EvidenceScope
{
    /// <summary>The document root (<c>$</c>).</summary>
    Root = 0,

    /// <summary>The <c>$.parties</c> array.</summary>
    Parties = 1,

    /// <summary>The <c>$.clauses</c> array.</summary>
    Clauses = 2,

    /// <summary>The <c>$.amendments</c> array.</summary>
    Amendments = 3,

    /// <summary>The <c>$.signatures</c> array.</summary>
    Signatures = 4,

    /// <summary>The <c>$.events</c> array (the ordered revocation/signing event log).</summary>
    Events = 5,
}

/// <summary>
/// Stable issue codes emitted by <see cref="AgreementAuditor"/>. Every code carries the
/// <c>MED_</c> prefix and is a compile-time constant: the codes are part of the public contract
/// and never change when human-readable messages are reworded.
/// </summary>
public static class MediationIssueCodes
{
    /// <summary>A clause references a clause id that does not exist anywhere in the package.</summary>
    public const string UnknownReference = "MED_UNKNOWN_REFERENCE";

    /// <summary>An amendment replaces a clause id that does not exist anywhere in the package.</summary>
    public const string AmendmentTargetMissing = "MED_AMENDMENT_TARGET_MISSING";

    /// <summary>Two or more live amendments replace the same clause id, so the authoritative version is ambiguous.</summary>
    public const string DuplicateReplacement = "MED_DUPLICATE_REPLACEMENT";

    /// <summary>Live amendments on the same clause declare different monetary amounts.</summary>
    public const string AmountInconsistent = "MED_AMOUNT_INCONSISTENT";

    /// <summary>A party has not signed a signable item (the base agreement or a valid live amendment).</summary>
    public const string SignatureScopeGap = "MED_SIGNATURE_SCOPE_GAP";

    /// <summary>A prerequisite clause is due after the dependent clause that references it.</summary>
    public const string DateOrderViolation = "MED_DATE_ORDER_VIOLATION";

    /// <summary>The clause reference graph contains a directed cycle.</summary>
    public const string ReferenceCycle = "MED_REFERENCE_CYCLE";

    /// <summary>The amendment replacement (version) graph contains a directed cycle.</summary>
    public const string ReplacementCycle = "MED_REPLACEMENT_CYCLE";

    /// <summary>A withdrawn amendment is still referenced by a signature scope or a live clause.</summary>
    public const string WithdrawnAmendmentReferenced = "MED_WITHDRAWN_AMENDMENT_REFERENCED";

    /// <summary>
    /// An event attempts to revoke the fact that a party already signed. History is immutable, so the
    /// signature is retained and this stable code is reported instead of silently rewriting the past.
    /// </summary>
    public const string SignedFactRevocationAttempt = "MED_SIGNED_FACT_REVOCATION_ATTEMPT";

    /// <summary>An event references a party, amendment or agreement id that does not exist in the package.</summary>
    public const string EventTargetUnknown = "MED_EVENT_TARGET_UNKNOWN";

    /// <summary>Two or more events share the same sequence number, so their ordering is ambiguous.</summary>
    public const string EventSequenceConflict = "MED_EVENT_SEQUENCE_CONFLICT";
}

/// <summary>A party to the mediation agreement.</summary>
/// <param name="Id">Stable party identifier (for example <c>PTY-A</c>).</param>
/// <param name="Name">Human-readable party name.</param>
/// <param name="SourceIndex">Zero-based index of this party inside the original <c>$.parties</c> array.</param>
public sealed record Party(string Id, string Name, int SourceIndex);

/// <summary>
/// A clause of the agreement. A clause is also the source of an <see cref="Obligation"/>: it names
/// the obligor, an optional monetary amount (in fen) and an optional due date.
/// </summary>
/// <param name="Id">Stable clause identifier (for example <c>CL-01</c>).</param>
/// <param name="Obligor">Party id that owes the obligation, if declared.</param>
/// <param name="AmountFen">Obligation amount in fen (1/100 yuan), if declared.</param>
/// <param name="Due">Due date parsed to a point in time, if a valid date was declared.</param>
/// <param name="References">Clause ids this clause depends on (its prerequisites).</param>
/// <param name="SourceIndex">Zero-based index of this clause inside the original <c>$.clauses</c> array.</param>
public sealed record Clause(
    string Id,
    string? Obligor,
    long? AmountFen,
    DateTimeOffset? Due,
    IReadOnlyList<string> References,
    int SourceIndex);

/// <summary>
/// A supplemental amendment that replaces one clause with a new clause version, optionally changing
/// the amount and due date. Amendments may be withdrawn (retracted) after the fact.
/// </summary>
/// <param name="Id">Stable amendment identifier (for example <c>AMD-01</c>).</param>
/// <param name="Replaces">Clause id this amendment supersedes.</param>
/// <param name="NewClauseId">Clause id introduced by this amendment.</param>
/// <param name="AmountFen">New obligation amount in fen, if the amendment changes it.</param>
/// <param name="Due">New due date, if the amendment changes it.</param>
/// <param name="Withdrawn">Whether the amendment has been retracted.</param>
/// <param name="SourceIndex">Zero-based index of this amendment inside the original <c>$.amendments</c> array.</param>
public sealed record Amendment(
    string Id,
    string? Replaces,
    string? NewClauseId,
    long? AmountFen,
    DateTimeOffset? Due,
    bool Withdrawn,
    int SourceIndex);

/// <summary>A party's signature covering a set of signable scope ids (the agreement and/or amendments).</summary>
/// <param name="PartyId">Party id that signed.</param>
/// <param name="Scope">Signable ids this signature covers.</param>
/// <param name="SignedAt">When the signature was applied, if declared.</param>
/// <param name="SourceIndex">Zero-based index of this signature inside the original <c>$.signatures</c> array.</param>
public sealed record Signature(
    string PartyId,
    IReadOnlyList<string> Scope,
    DateTimeOffset? SignedAt,
    int SourceIndex);

/// <summary>
/// The semantic of an entry in the ordered event log (<c>$.events</c>). The four revocation-family
/// verbs are distinct on purpose: three of them fold into the effective state, while
/// <see cref="RevokeSignedFact"/> is never allowed to rewrite history.
/// </summary>
public enum AgreementEventType
{
    /// <summary>A party signs a signable id (base agreement or amendment) at this point in the log.</summary>
    Sign = 0,

    /// <summary>Withdraw a single amendment (equivalent to marking it withdrawn from this event onward).</summary>
    RevokeAmendment = 1,

    /// <summary>Withdraw the entire agreement, invalidating every signature scope going forward.</summary>
    RevokeAgreement = 2,

    /// <summary>Remove a party from the agreement, dropping the party and its signing obligation.</summary>
    RemoveParty = 3,

    /// <summary>
    /// Attempt to revoke the recorded fact that a party already signed. This must not alter history;
    /// the auditor keeps the signature and reports <see cref="MediationIssueCodes.SignedFactRevocationAttempt"/>.
    /// </summary>
    RevokeSignedFact = 4,

    /// <summary>An unrecognized verb; retained verbatim so the log stays complete but otherwise inert.</summary>
    Unknown = 5,
}

/// <summary>
/// One entry in the append-only event log. Events are applied in ascending <see cref="Sequence"/>
/// order — never in array/arrival order — so that a revocation and a new signing that arrive out of
/// order under the same wall-clock timestamp still resolve deterministically by their sequence number.
/// </summary>
/// <param name="Sequence">The authoritative event ordinal defined by the source material.</param>
/// <param name="Type">The event semantic.</param>
/// <param name="RawType">The verbatim <c>type</c> string as it appeared in the JSON.</param>
/// <param name="PartyId">Party id the event concerns, if any.</param>
/// <param name="AmendmentId">Amendment id the event concerns, if any.</param>
/// <param name="Scope">Scope id the event concerns (for sign/revoke-signed-fact), if any.</param>
/// <param name="At">Wall-clock timestamp of the event, if declared. Never used for ordering.</param>
/// <param name="SourceIndex">Zero-based index of this event inside the original <c>$.events</c> array.</param>
public sealed record AgreementEvent(
    long Sequence,
    AgreementEventType Type,
    string RawType,
    string? PartyId,
    string? AmendmentId,
    string? Scope,
    DateTimeOffset? At,
    int SourceIndex);

/// <summary>
/// The effective obligation that results after live amendments are applied to a clause lineage.
/// </summary>
/// <param name="ClauseId">Effective clause id carrying the obligation.</param>
/// <param name="Obligor">Party id that owes the obligation.</param>
/// <param name="AmountFen">Effective amount in fen, if known.</param>
/// <param name="Due">Effective due date, if known.</param>
public sealed record Obligation(string ClauseId, string? Obligor, long? AmountFen, DateTimeOffset? Due);

/// <summary>
/// How a version-chain edge came to exist. Both kinds are retained in the audit graph; the
/// distinction is informational because the auditable history must never physically drop a clause.
/// </summary>
public enum RevisionKind
{
    /// <summary>The amendment replaces an existing clause version (<c>replaces</c> targets a known clause).</summary>
    Replacement = 0,

    /// <summary>The amendment appends a brand-new clause version (<c>replaces</c> is absent or empty).</summary>
    Append = 1,
}

/// <summary>
/// A node in the auditable clause version graph: one concrete clause version. A node exists for
/// every original clause and for every clause id introduced by an amendment; nothing is deleted, so
/// superseded versions remain queryable for history.
/// </summary>
public sealed class ClauseVersionNode
{
    internal ClauseVersionNode(string clauseId, bool isOriginal, int? clauseSourceIndex, int? amendmentSourceIndex)
    {
        ClauseId = clauseId;
        IsOriginal = isOriginal;
        ClauseSourceIndex = clauseSourceIndex;
        AmendmentSourceIndex = amendmentSourceIndex;
    }

    /// <summary>The clause id this version carries (reused verbatim from round 1).</summary>
    public string ClauseId { get; }

    /// <summary>True when this version came from the original <c>$.clauses</c> array.</summary>
    public bool IsOriginal { get; }

    /// <summary>Index in <c>$.clauses</c> when the version originates there; otherwise <c>null</c>.</summary>
    public int? ClauseSourceIndex { get; }

    /// <summary>Index in <c>$.amendments</c> of the amendment that introduced this version; <c>null</c> for originals.</summary>
    public int? AmendmentSourceIndex { get; }

    /// <summary>True when a live amendment replaces this version, so it is no longer a tip of its chain.</summary>
    public bool IsSuperseded { get; internal set; }
}

/// <summary>
/// A directed edge in the version graph: an amendment that turns the <see cref="FromClauseId"/>
/// version into the <see cref="ToClauseId"/> version. Edges are keyed by the amendment so evidence
/// paths always point back to the originating <c>$.amendments[i]</c> entry.
/// </summary>
public sealed class ClauseVersionEdge
{
    internal ClauseVersionEdge(
        string amendmentId,
        string? fromClauseId,
        string toClauseId,
        RevisionKind kind,
        int amendmentSourceIndex)
    {
        AmendmentId = amendmentId;
        FromClauseId = fromClauseId;
        ToClauseId = toClauseId;
        Kind = kind;
        AmendmentSourceIndex = amendmentSourceIndex;
    }

    /// <summary>Id of the amendment that created this edge.</summary>
    public string AmendmentId { get; }

    /// <summary>The clause id being revised, or <c>null</c> for an append.</summary>
    public string? FromClauseId { get; }

    /// <summary>The clause id produced by this revision.</summary>
    public string ToClauseId { get; }

    /// <summary>Whether the edge is a replacement or an append.</summary>
    public RevisionKind Kind { get; }

    /// <summary>Index of the amendment in the original <c>$.amendments</c> array.</summary>
    public int AmendmentSourceIndex { get; }

    /// <summary>Evidence path anchored on the amendment's <c>replaces</c> (or the amendment element for appends).</summary>
    public string EvidencePath => Kind == RevisionKind.Replacement
        ? $"$.amendments[{AmendmentSourceIndex}].replaces"
        : $"$.amendments[{AmendmentSourceIndex}]";
}

/// <summary>
/// The auditable directed version chain built from the clauses and their amendments. The chain is
/// insensitive to the order of the <c>$.amendments</c> array: nodes, edges and the effective view
/// are all derived from clause ids, and any position-sensitive choice (such as which of two
/// concurrent replacements "wins") is resolved by the smallest amendment id, never by array index.
/// </summary>
public sealed class ClauseVersionChain
{
    internal ClauseVersionChain(
        IReadOnlyList<ClauseVersionNode> nodes,
        IReadOnlyList<ClauseVersionEdge> edges,
        IReadOnlyList<ClauseVersionNode> effectiveVersions)
    {
        Nodes = nodes;
        Edges = edges;
        EffectiveVersions = effectiveVersions;
    }

    /// <summary>Every clause version, in ascending clause-id order. Superseded versions are retained.</summary>
    public IReadOnlyList<ClauseVersionNode> Nodes { get; }

    /// <summary>Every revision edge, in ascending (amendment-id) order.</summary>
    public IReadOnlyList<ClauseVersionEdge> Edges { get; }

    /// <summary>The current, non-superseded clause versions, in ascending clause-id order.</summary>
    public IReadOnlyList<ClauseVersionNode> EffectiveVersions { get; }

    /// <summary>Returns the effective clause ids as a stable, ascending list (a canonical view for comparison).</summary>
    /// <returns>The non-superseded clause ids in ordinal order.</returns>
    public IReadOnlyList<string> EffectiveClauseIds() =>
        EffectiveVersions.Select(n => n.ClauseId).OrderBy(id => id, StringComparer.Ordinal).ToList();
}

/// <summary>
/// The effective signing/participation state after folding the ordered event log over the base
/// package. The fold is a pure function of the events sorted by <see cref="AgreementEvent.Sequence"/>
/// (never their array/arrival order), so the same package always yields the same state.
/// </summary>
public sealed class EffectiveState
{
    internal EffectiveState(
        bool agreementRevoked,
        IReadOnlyList<string> activeParties,
        IReadOnlyList<string> revokedAmendmentIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSignatures)
    {
        AgreementRevoked = agreementRevoked;
        ActiveParties = activeParties;
        RevokedAmendmentIds = revokedAmendmentIds;
        EffectiveSignatures = effectiveSignatures;
    }

    /// <summary>True when a revoke-agreement event has invalidated the whole package.</summary>
    public bool AgreementRevoked { get; }

    /// <summary>Party ids still participating after any remove-party events, in ascending order.</summary>
    public IReadOnlyList<string> ActiveParties { get; }

    /// <summary>Amendment ids revoked by events (in addition to any statically withdrawn), ascending.</summary>
    public IReadOnlyList<string> RevokedAmendmentIds { get; }

    /// <summary>
    /// For each still-active party, the set of scope ids it has effectively signed after the fold,
    /// keyed by party id (ascending) with each scope list ascending. A revoke-signed-fact never
    /// removes an entry here — history is immutable — it only produces an audit issue.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> EffectiveSignatures { get; }
}

/// <summary>
/// A parsed mediation agreement package: the root aggregate the auditor operates on. Element order
/// is preserved exactly as it appeared in the source JSON so evidence paths stay stable.
/// </summary>
public sealed class MediationAgreement
{
    /// <summary>The agreement identifier (for example <c>AGR-2026-018</c>).</summary>
    public string AgreementId { get; }

    /// <summary>Parties in source order.</summary>
    public IReadOnlyList<Party> Parties { get; }

    /// <summary>Clauses in source order.</summary>
    public IReadOnlyList<Clause> Clauses { get; }

    /// <summary>Amendments in source order.</summary>
    public IReadOnlyList<Amendment> Amendments { get; }

    /// <summary>Signatures in source order.</summary>
    public IReadOnlyList<Signature> Signatures { get; }

    /// <summary>Ordered event log entries in source (array) order. Fold them by sequence, not by this order.</summary>
    public IReadOnlyList<AgreementEvent> Events { get; }

    /// <summary>Initializes a new <see cref="MediationAgreement"/>.</summary>
    /// <param name="agreementId">The agreement identifier.</param>
    /// <param name="parties">Parties in source order.</param>
    /// <param name="clauses">Clauses in source order.</param>
    /// <param name="amendments">Amendments in source order.</param>
    /// <param name="signatures">Signatures in source order.</param>
    /// <param name="events">Event log entries in source order (optional).</param>
    public MediationAgreement(
        string agreementId,
        IReadOnlyList<Party> parties,
        IReadOnlyList<Clause> clauses,
        IReadOnlyList<Amendment> amendments,
        IReadOnlyList<Signature> signatures,
        IReadOnlyList<AgreementEvent>? events = null)
    {
        AgreementId = agreementId;
        Parties = parties;
        Clauses = clauses;
        Amendments = amendments;
        Signatures = signatures;
        Events = events ?? Array.Empty<AgreementEvent>();
    }
}

/// <summary>
/// Raised when an agreement package cannot be parsed into a <see cref="MediationAgreement"/>.
/// </summary>
public sealed class AgreementParseException : Exception
{
    /// <summary>Initializes a new <see cref="AgreementParseException"/>.</summary>
    /// <param name="message">Description of the parse failure.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    public AgreementParseException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A single consistency finding. The <see cref="Code"/> and <see cref="EvidencePath"/> form the
/// stable, text-independent identity of a finding; <see cref="Message"/> is display-only.
/// </summary>
public sealed class AuditIssue
{
    internal AuditIssue(
        string code,
        Severity severity,
        string message,
        string evidencePath,
        EvidenceScope scope,
        int primaryIndex,
        int secondaryIndex,
        string field,
        string sortKey)
    {
        Code = code;
        Severity = severity;
        Message = message;
        EvidencePath = evidencePath;
        Scope = scope;
        PrimaryIndex = primaryIndex;
        SecondaryIndex = secondaryIndex;
        Field = field;
        SortKey = sortKey;
    }

    /// <summary>Stable issue code (always <c>MED_</c>-prefixed).</summary>
    public string Code { get; }

    /// <summary>Severity of the finding.</summary>
    public Severity Severity { get; }

    /// <summary>Human-readable, display-only description. Never used for sorting or identity.</summary>
    public string Message { get; }

    /// <summary>JSON evidence path pointing into the original document (for example <c>$.clauses[1].due</c>).</summary>
    public string EvidencePath { get; }

    /// <summary>The top-level scope the evidence path is rooted in.</summary>
    public EvidenceScope Scope { get; }

    /// <summary>Zero-based index of the primary element in its source array (or -1 for the root).</summary>
    public int PrimaryIndex { get; }

    /// <summary>Zero-based sub-index (for example a <c>references</c> element), or -1 when not applicable.</summary>
    public int SecondaryIndex { get; }

    /// <summary>Field name the evidence path terminates in (may be empty for element-level paths).</summary>
    public string Field { get; }

    /// <summary>
    /// A position-independent ordering key built only from logical identifiers (clause ids,
    /// amendment ids, party ids). Two audits of the same package produce identical sort keys even if
    /// the <c>$.amendments</c> array (or any field) is reordered, because the key never encodes a
    /// physical array index or the display message.
    /// </summary>
    public string SortKey { get; }

    /// <summary>Returns a compact, deterministic string representation.</summary>
    /// <returns>A string of the form <c>CODE [Severity] path — message</c>.</returns>
    public override string ToString() =>
        $"{Code} [{Severity}] {EvidencePath} — {Message}";
}

/// <summary>
/// The result of auditing a <see cref="MediationAgreement"/>: a deterministically ordered set of
/// issues plus convenience roll-ups.
/// </summary>
public sealed class AuditResult
{
    /// <summary>Issues in a stable order that is independent of traversal order and message text.</summary>
    public IReadOnlyList<AuditIssue> Issues { get; }

    /// <summary>Initializes a new <see cref="AuditResult"/>.</summary>
    /// <param name="issues">The ordered issues.</param>
    public AuditResult(IReadOnlyList<AuditIssue> issues) => Issues = issues;

    /// <summary>True when at least one <see cref="Severity.Error"/> issue is present.</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == Severity.Error);

    /// <summary>Number of issues per stable code, in ascending code order.</summary>
    /// <returns>A read-only mapping from issue code to occurrence count.</returns>
    public IReadOnlyDictionary<string, int> CountByCode() =>
        Issues.GroupBy(i => i.Code, StringComparer.Ordinal)
              .OrderBy(g => g.Key, StringComparer.Ordinal)
              .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}

/// <summary>
/// Audits mediation agreement packages for the consistency problems that arise once clauses are
/// repeatedly amended: dangling references, ambiguous replacements, amount/date dependency breaks,
/// signature scope gaps and reference cycles.
///
/// <para>All graph work is iterative, so pathologically deep reference chains cannot overflow the
/// stack. All output ordering is derived from stable codes and original JSON positions, so it never
/// depends on traversal order or on the wording of messages, and re-auditing the same package (in
/// any field order) yields identical results.</para>
/// </summary>
public sealed class AgreementAuditor
{
    /// <summary>Parses an agreement package from a JSON string.</summary>
    /// <param name="json">The raw JSON package.</param>
    /// <returns>The parsed <see cref="MediationAgreement"/>.</returns>
    /// <exception cref="AgreementParseException">The JSON is malformed or missing required shape.</exception>
    public static MediationAgreement Parse(string json)
    {
        if (json is null) throw new AgreementParseException("JSON content was null.");

        JsonDocument document;
        try
        {
            // System.Text.Json enforces a maximum nesting depth and throws (rather than recursing
            // without bound), so hostile deeply-nested input surfaces as a parse error, not a crash.
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new AgreementParseException("Agreement package is not valid JSON.", ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new AgreementParseException("Agreement package root must be a JSON object.");

            string agreementId = GetString(root, "agreementId") ?? string.Empty;

            var parties = new List<Party>();
            foreach (var (element, index) in EnumerateArray(root, "parties"))
            {
                parties.Add(new Party(
                    GetString(element, "id") ?? string.Empty,
                    GetString(element, "name") ?? string.Empty,
                    index));
            }

            var clauses = new List<Clause>();
            foreach (var (element, index) in EnumerateArray(root, "clauses"))
            {
                clauses.Add(new Clause(
                    GetString(element, "id") ?? string.Empty,
                    GetString(element, "obligor"),
                    GetLong(element, "amountFen"),
                    GetDate(element, "due"),
                    GetStringArray(element, "references"),
                    index));
            }

            var amendments = new List<Amendment>();
            foreach (var (element, index) in EnumerateArray(root, "amendments"))
            {
                amendments.Add(new Amendment(
                    GetString(element, "id") ?? string.Empty,
                    GetString(element, "replaces"),
                    GetString(element, "newClauseId"),
                    GetLong(element, "amountFen"),
                    GetDate(element, "due"),
                    GetBool(element, "withdrawn") ?? false,
                    index));
            }

            var signatures = new List<Signature>();
            foreach (var (element, index) in EnumerateArray(root, "signatures"))
            {
                signatures.Add(new Signature(
                    GetString(element, "partyId") ?? string.Empty,
                    GetStringArray(element, "scope"),
                    GetDate(element, "signedAt"),
                    index));
            }

            var events = new List<AgreementEvent>();
            foreach (var (element, index) in EnumerateArray(root, "events"))
            {
                string rawType = GetString(element, "type") ?? string.Empty;
                events.Add(new AgreementEvent(
                    GetLong(element, "seq") ?? long.MinValue,
                    ParseEventType(rawType),
                    rawType,
                    GetString(element, "partyId"),
                    GetString(element, "amendmentId"),
                    GetString(element, "scope"),
                    GetDate(element, "at"),
                    index));
            }

            return new MediationAgreement(agreementId, parties, clauses, amendments, signatures, events);
        }
    }

    private static AgreementEventType ParseEventType(string rawType) => rawType switch
    {
        "sign" => AgreementEventType.Sign,
        "revokeAmendment" => AgreementEventType.RevokeAmendment,
        "revokeAgreement" => AgreementEventType.RevokeAgreement,
        "removeParty" => AgreementEventType.RemoveParty,
        "revokeSignedFact" => AgreementEventType.RevokeSignedFact,
        _ => AgreementEventType.Unknown,
    };

    /// <summary>Parses and audits an agreement package supplied as JSON.</summary>
    /// <param name="json">The raw JSON package.</param>
    /// <returns>The deterministic <see cref="AuditResult"/>.</returns>
    /// <exception cref="AgreementParseException">The JSON is malformed.</exception>
    public AuditResult AuditJson(string json) => Audit(Parse(json));

    /// <summary>
    /// Computes the effective obligations after applying every live (non-withdrawn) amendment whose
    /// replacement target exists. This models the "current" state a mediator cares about.
    /// </summary>
    /// <param name="agreement">The agreement package.</param>
    /// <returns>Effective obligations keyed by the surviving clause id, in stable clause-id order.</returns>
    public IReadOnlyList<Obligation> ComputeObligations(MediationAgreement agreement)
    {
        if (agreement is null) throw new ArgumentNullException(nameof(agreement));

        var clauseById = new Dictionary<string, Clause>(StringComparer.Ordinal);
        foreach (var clause in agreement.Clauses)
            clauseById[clause.Id] = clause;

        // Map each replaced clause id to the winning live amendment. The smallest amendment id wins
        // (a logical, position-independent tie-break) so the effective view is identical no matter
        // how the $.amendments array happens to be ordered.
        var winningAmendment = new Dictionary<string, Amendment>(StringComparer.Ordinal);
        foreach (var amendment in agreement.Amendments.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            if (amendment.Withdrawn) continue;
            if (amendment.Replaces is null || !clauseById.ContainsKey(amendment.Replaces)) continue;
            if (!winningAmendment.ContainsKey(amendment.Replaces))
                winningAmendment[amendment.Replaces] = amendment;
        }

        var obligations = new List<Obligation>();
        foreach (var clause in agreement.Clauses)
        {
            if (winningAmendment.TryGetValue(clause.Id, out var amendment))
            {
                obligations.Add(new Obligation(
                    amendment.NewClauseId ?? clause.Id,
                    clause.Obligor,
                    amendment.AmountFen ?? clause.AmountFen,
                    amendment.Due ?? clause.Due));
            }
            else
            {
                obligations.Add(new Obligation(clause.Id, clause.Obligor, clause.AmountFen, clause.Due));
            }
        }

        return obligations.OrderBy(o => o.ClauseId, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Folds the ordered event log over the base package to produce the effective signing and
    /// participation state. Events are applied strictly in ascending <see cref="AgreementEvent.Sequence"/>
    /// order (with a stable secondary tie-break by source index only when two events share a sequence),
    /// so the outcome depends solely on the material-defined sequence numbers, never on the order in
    /// which entries happen to arrive in the array. A revoke-signed-fact event does not erase a prior
    /// signing — history is immutable — so it leaves <see cref="EffectiveState.EffectiveSignatures"/>
    /// untouched (the accompanying audit issue is raised separately by <see cref="Audit"/>).
    /// </summary>
    /// <param name="agreement">The agreement package.</param>
    /// <returns>The deterministic <see cref="EffectiveState"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agreement"/> is null.</exception>
    public EffectiveState ComputeEffectiveState(MediationAgreement agreement)
    {
        if (agreement is null) throw new ArgumentNullException(nameof(agreement));

        var activeParties = new HashSet<string>(agreement.Parties.Select(p => p.Id), StringComparer.Ordinal);
        var revokedAmendments = new HashSet<string>(StringComparer.Ordinal);
        // Amendments already statically withdrawn count as revoked from the start.
        foreach (var amendment in agreement.Amendments)
            if (amendment.Withdrawn)
                revokedAmendments.Add(amendment.Id);

        bool agreementRevoked = false;
        var signatures = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Seed with the statically recorded signatures: these are established facts before any event.
        foreach (var signature in agreement.Signatures)
        {
            if (!activeParties.Contains(signature.PartyId)) continue;
            if (!signatures.TryGetValue(signature.PartyId, out var seed))
                signatures[signature.PartyId] = seed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scope in signature.Scope)
                seed.Add(scope);
        }

        foreach (var evt in OrderedEvents(agreement))
        {
            switch (evt.Type)
            {
                case AgreementEventType.Sign:
                    if (evt.PartyId is not null && evt.Scope is not null && activeParties.Contains(evt.PartyId))
                    {
                        if (!signatures.TryGetValue(evt.PartyId, out var scopes))
                            signatures[evt.PartyId] = scopes = new HashSet<string>(StringComparer.Ordinal);
                        scopes.Add(evt.Scope);
                    }
                    break;

                case AgreementEventType.RevokeAmendment:
                    if (evt.AmendmentId is not null)
                        revokedAmendments.Add(evt.AmendmentId);
                    break;

                case AgreementEventType.RevokeAgreement:
                    agreementRevoked = true;
                    break;

                case AgreementEventType.RemoveParty:
                    if (evt.PartyId is not null)
                    {
                        activeParties.Remove(evt.PartyId);
                        signatures.Remove(evt.PartyId);
                    }
                    break;

                case AgreementEventType.RevokeSignedFact:
                    // Deliberately a no-op on state: a signed fact cannot be rewritten. The audit
                    // surfaces MED_SIGNED_FACT_REVOCATION_ATTEMPT so the attempt is visible.
                    break;

                case AgreementEventType.Unknown:
                default:
                    break;
            }
        }

        var effectiveSignatures = signatures
            .Where(kv => activeParties.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<string>)kv.Value.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        return new EffectiveState(
            agreementRevoked,
            activeParties.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            revokedAmendments.OrderBy(a => a, StringComparer.Ordinal).ToList(),
            effectiveSignatures);
    }

    /// <summary>
    /// Returns the event log in the authoritative application order: ascending sequence number, then
    /// (only to break exact sequence ties) a stable logical content key. Ordering never consults the
    /// array/arrival position, so the fold is identical whether the same package is submitted once,
    /// twice, or with its <c>$.events</c> array reordered. Genuine same-sequence ties are additionally
    /// reported as <see cref="MediationIssueCodes.EventSequenceConflict"/>.
    /// </summary>
    private static IReadOnlyList<AgreementEvent> OrderedEvents(MediationAgreement agreement) =>
        agreement.Events
            .OrderBy(e => e.Sequence)
            .ThenBy(e => EventTieBreakKey(e), StringComparer.Ordinal)
            .ToList();

    private static string EventTieBreakKey(AgreementEvent e) =>
        string.Join("\u0000", (int)e.Type, e.PartyId ?? "", e.AmendmentId ?? "", e.Scope ?? "");

    /// <summary>Runs every consistency check against a parsed agreement package.</summary>
    /// <param name="agreement">The agreement package to audit.</param>
    /// <returns>The deterministic <see cref="AuditResult"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agreement"/> is null.</exception>
    public AuditResult Audit(MediationAgreement agreement)
    {
        if (agreement is null) throw new ArgumentNullException(nameof(agreement));

        var issues = new List<AuditIssue>();

        // Known clause ids = original clauses plus every id introduced by an amendment.
        var knownClauseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clause in agreement.Clauses)
            knownClauseIds.Add(clause.Id);
        foreach (var amendment in agreement.Amendments)
            if (!string.IsNullOrEmpty(amendment.NewClauseId))
                knownClauseIds.Add(amendment.NewClauseId!);

        var clauseByIndex = agreement.Clauses;
        var clauseById = new Dictionary<string, Clause>(StringComparer.Ordinal);
        foreach (var clause in agreement.Clauses)
            clauseById[clause.Id] = clause; // last-writer-wins for duplicate ids; index-based paths stay exact.

        CheckAmendmentTargets(agreement, knownClauseIds, issues, out var validLiveAmendments);
        CheckDuplicateReplacements(validLiveAmendments, issues);
        CheckReplacementCycles(agreement, knownClauseIds, issues);
        CheckUnknownReferences(agreement, knownClauseIds, issues);
        CheckReferenceCycles(agreement, clauseById, issues);
        CheckDateOrder(agreement, clauseById, issues);
        CheckSignatureScope(agreement, validLiveAmendments, issues);
        CheckWithdrawnReferenced(agreement, clauseById, issues);
        CheckEvents(agreement, issues);

        issues.Sort(CompareIssues);
        return new AuditResult(issues);
    }

    /// <summary>
    /// Builds the auditable directed clause-version chain from the package's clauses and amendments.
    /// The result is independent of the order of the <c>$.amendments</c> array: every original clause
    /// and every amendment-introduced clause id becomes a retained node (nothing is deleted), each
    /// live replacement/append becomes an edge, and a version is marked superseded only when a live
    /// amendment whose target exists replaces it. When two concurrent live amendments replace the same
    /// clause, the smallest amendment id is treated as the effective successor so the effective view
    /// never depends on array position.
    /// </summary>
    /// <param name="agreement">The agreement package.</param>
    /// <returns>The <see cref="ClauseVersionChain"/> describing versions, edges and the effective view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agreement"/> is null.</exception>
    public ClauseVersionChain BuildVersionChain(MediationAgreement agreement)
    {
        if (agreement is null) throw new ArgumentNullException(nameof(agreement));

        // Nodes: original clauses first, then amendment-introduced clause ids. Original wins the
        // provenance when a clause id happens to collide with an amendment's new id.
        var nodesById = new Dictionary<string, ClauseVersionNode>(StringComparer.Ordinal);
        foreach (var clause in agreement.Clauses)
            if (!nodesById.ContainsKey(clause.Id))
                nodesById[clause.Id] = new ClauseVersionNode(clause.Id, isOriginal: true, clause.SourceIndex, null);

        foreach (var amendment in agreement.Amendments.OrderBy(a => a.Id, StringComparer.Ordinal))
            if (!string.IsNullOrEmpty(amendment.NewClauseId) && !nodesById.ContainsKey(amendment.NewClauseId!))
                nodesById[amendment.NewClauseId!] =
                    new ClauseVersionNode(amendment.NewClauseId!, isOriginal: false, null, amendment.SourceIndex);

        // Edges: one per live amendment. A replacement supersedes its target; an append introduces a
        // fresh version with no predecessor. Concurrent replacements of the same clause all become
        // edges (the fork is preserved for the audit), but only the smallest-id amendment supersedes
        // the shared parent, keeping the effective view position-independent.
        var edges = new List<ClauseVersionEdge>();
        var supersededBy = new Dictionary<string, string>(StringComparer.Ordinal); // parent id -> winning amendment id
        foreach (var amendment in agreement.Amendments.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            if (amendment.Withdrawn) continue;
            if (string.IsNullOrEmpty(amendment.NewClauseId)) continue;

            bool isReplacement = amendment.Replaces is not null && nodesById.ContainsKey(amendment.Replaces);
            if (isReplacement)
            {
                edges.Add(new ClauseVersionEdge(
                    amendment.Id, amendment.Replaces, amendment.NewClauseId!, RevisionKind.Replacement, amendment.SourceIndex));
                if (!supersededBy.ContainsKey(amendment.Replaces!))
                    supersededBy[amendment.Replaces!] = amendment.Id;
            }
            else if (amendment.Replaces is null)
            {
                edges.Add(new ClauseVersionEdge(
                    amendment.Id, null, amendment.NewClauseId!, RevisionKind.Append, amendment.SourceIndex));
            }
            // A replacement whose target is unknown is reported elsewhere and produces no edge.
        }

        foreach (var parentId in supersededBy.Keys)
            if (nodesById.TryGetValue(parentId, out var node))
                node.IsSuperseded = true;

        var orderedNodes = nodesById.Values.OrderBy(n => n.ClauseId, StringComparer.Ordinal).ToList();
        var effective = orderedNodes.Where(n => !n.IsSuperseded).ToList();
        var orderedEdges = edges
            .OrderBy(e => e.AmendmentId, StringComparer.Ordinal)
            .ThenBy(e => e.ToClauseId, StringComparer.Ordinal)
            .ToList();

        return new ClauseVersionChain(orderedNodes, orderedEdges, effective);
    }

    // ----- individual checks -------------------------------------------------------------------

    private static void CheckAmendmentTargets(
        MediationAgreement agreement,
        HashSet<string> knownClauseIds,
        List<AuditIssue> issues,
        out List<Amendment> validLiveAmendments)
    {
        validLiveAmendments = new List<Amendment>();
        foreach (var amendment in agreement.Amendments)
        {
            if (amendment.Withdrawn) continue;

            bool targetExists = amendment.Replaces is not null && knownClauseIds.Contains(amendment.Replaces);
            if (!targetExists)
            {
                issues.Add(new AuditIssue(
                    MediationIssueCodes.AmendmentTargetMissing,
                    Severity.Error,
                    $"Amendment '{amendment.Id}' replaces clause '{amendment.Replaces ?? "(none)"}', which does not exist.",
                    $"$.amendments[{amendment.SourceIndex}].replaces",
                    EvidenceScope.Amendments,
                    amendment.SourceIndex,
                    -1,
                    "replaces",
                    $"AMD:{amendment.Id}"));
                continue;
            }

            validLiveAmendments.Add(amendment);
        }
    }

    private static void CheckDuplicateReplacements(List<Amendment> validLiveAmendments, List<AuditIssue> issues)
    {
        var groups = validLiveAmendments
            .Where(a => a.Replaces is not null)
            .GroupBy(a => a.Replaces!, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            // Order competing amendments by id (not by array position) so the baseline "winner" and
            // every reported duplicate are the same regardless of how the amendments array is ordered.
            var ordered = group.OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
            if (ordered.Count < 2) continue;

            var baseline = ordered[0];
            // Every amendment other than the smallest-id one is a structurally ambiguous replacement.
            foreach (var amendment in ordered.Skip(1))
            {
                issues.Add(new AuditIssue(
                    MediationIssueCodes.DuplicateReplacement,
                    Severity.Error,
                    $"Amendment '{amendment.Id}' replaces clause '{amendment.Replaces}', which amendment '{baseline.Id}' already replaces.",
                    $"$.amendments[{amendment.SourceIndex}].replaces",
                    EvidenceScope.Amendments,
                    amendment.SourceIndex,
                    -1,
                    "replaces",
                    $"{amendment.Replaces}\u0000AMD:{amendment.Id}"));

                // A monetary conflict among the competing replacements is called out separately.
                if (amendment.AmountFen.HasValue && baseline.AmountFen.HasValue
                    && amendment.AmountFen.Value != baseline.AmountFen.Value)
                {
                    issues.Add(new AuditIssue(
                        MediationIssueCodes.AmountInconsistent,
                        Severity.Warning,
                        $"Amendment '{amendment.Id}' sets amount {amendment.AmountFen} fen for clause '{amendment.Replaces}', conflicting with {baseline.AmountFen} fen from '{baseline.Id}'.",
                        $"$.amendments[{amendment.SourceIndex}].amountFen",
                        EvidenceScope.Amendments,
                        amendment.SourceIndex,
                        -1,
                        "amountFen",
                        $"{amendment.Replaces}\u0000AMD:{amendment.Id}"));
                }
            }
        }
    }

    /// <summary>
    /// Detects directed cycles in the amendment replacement (version) graph, where each live
    /// amendment draws an edge from its <c>newClauseId</c> to the clause it <c>replaces</c>. A cycle
    /// means the version history folds back on itself (for example CL-A is replaced by CL-B while
    /// CL-B is replaced back into CL-A). The evidence is the single shortest anchor: the
    /// <c>replaces</c> field of the smallest-id amendment participating in the cycle.
    /// </summary>
    private static void CheckReplacementCycles(
        MediationAgreement agreement,
        HashSet<string> knownClauseIds,
        List<AuditIssue> issues)
    {
        // Edge: newClauseId -> replaces, for live amendments whose endpoints are both known clause ids.
        // Keep, per source node, the smallest-id amendment so the reported evidence is canonical.
        var amendmentByEdge = new Dictionary<string, Amendment>(StringComparer.Ordinal);
        foreach (var amendment in agreement.Amendments.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            if (amendment.Withdrawn) continue;
            if (string.IsNullOrEmpty(amendment.NewClauseId) || amendment.Replaces is null) continue;
            if (!knownClauseIds.Contains(amendment.NewClauseId!) || !knownClauseIds.Contains(amendment.Replaces)) continue;
            if (!amendmentByEdge.ContainsKey(amendment.NewClauseId!))
                amendmentByEdge[amendment.NewClauseId!] = amendment;
        }

        var cycles = StronglyConnectedComponents.FindCycles(
            nodeIds: amendmentByEdge.Keys
                .Concat(amendmentByEdge.Values.Select(a => a.Replaces!))
                .Distinct(StringComparer.Ordinal),
            edgesFrom: node => amendmentByEdge.TryGetValue(node, out var amendment)
                ? new[] { amendment.Replaces! }
                : Array.Empty<string>());

        foreach (var component in cycles)
        {
            // The amendments forming the cycle are those whose source node is in the component.
            var cycleAmendments = component
                .Where(node => amendmentByEdge.ContainsKey(node))
                .Select(node => amendmentByEdge[node])
                .OrderBy(a => a.Id, StringComparer.Ordinal)
                .ToList();
            if (cycleAmendments.Count == 0) continue;

            var canonical = cycleAmendments[0];
            var members = component.OrderBy(id => id, StringComparer.Ordinal);
            issues.Add(new AuditIssue(
                MediationIssueCodes.ReplacementCycle,
                Severity.Error,
                $"Amendment replacement cycle detected among clauses: {string.Join(", ", members)}.",
                $"$.amendments[{canonical.SourceIndex}].replaces",
                EvidenceScope.Amendments,
                canonical.SourceIndex,
                -1,
                "replaces",
                $"AMD:{canonical.Id}"));
        }
    }

    private static void CheckUnknownReferences(
        MediationAgreement agreement,
        HashSet<string> knownClauseIds,
        List<AuditIssue> issues)
    {
        foreach (var clause in agreement.Clauses)
        {
            for (int j = 0; j < clause.References.Count; j++)
            {
                var target = clause.References[j];
                if (!knownClauseIds.Contains(target))
                {
                    issues.Add(new AuditIssue(
                        MediationIssueCodes.UnknownReference,
                        Severity.Error,
                        $"Clause '{clause.Id}' references '{target}', which does not exist.",
                        $"$.clauses[{clause.SourceIndex}].references[{j}]",
                        EvidenceScope.Clauses,
                        clause.SourceIndex,
                        j,
                        "references",
                        $"{clause.Id}\u0000{target}"));
                }
            }
        }
    }

    private static void CheckDateOrder(
        MediationAgreement agreement,
        Dictionary<string, Clause> clauseById,
        List<AuditIssue> issues)
    {
        foreach (var clause in agreement.Clauses)
        {
            if (clause.Due is null) continue;

            for (int j = 0; j < clause.References.Count; j++)
            {
                if (!clauseById.TryGetValue(clause.References[j], out var prerequisite)) continue;
                if (prerequisite.Due is null) continue;

                if (prerequisite.Due.Value > clause.Due.Value)
                {
                    issues.Add(new AuditIssue(
                        MediationIssueCodes.DateOrderViolation,
                        Severity.Error,
                        $"Clause '{clause.Id}' is due {Format(clause.Due.Value)} but prerequisite '{prerequisite.Id}' is due later ({Format(prerequisite.Due.Value)}).",
                        $"$.clauses[{clause.SourceIndex}].references[{j}]",
                        EvidenceScope.Clauses,
                        clause.SourceIndex,
                        j,
                        "references",
                        $"{clause.Id}\u0000{prerequisite.Id}"));
                }
            }
        }
    }

    private static void CheckSignatureScope(
        MediationAgreement agreement,
        List<Amendment> validLiveAmendments,
        List<AuditIssue> issues)
    {
        // Signable ids: the base agreement plus every valid live amendment, in stable id order so the
        // "missing" message text does not depend on the amendments array order.
        var signables = new List<string> { agreement.AgreementId };
        signables.AddRange(validLiveAmendments.Select(a => a.Id).OrderBy(id => id, StringComparer.Ordinal));

        // The lowest-index signature per party carries the coverage evidence; coverage itself is a
        // set union, so it is independent of signature order.
        var firstSignatureByParty = new Dictionary<string, Signature>(StringComparer.Ordinal);
        var coverageByParty = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var signature in agreement.Signatures.OrderBy(s => s.SourceIndex))
        {
            if (!coverageByParty.TryGetValue(signature.PartyId, out var covered))
            {
                covered = new HashSet<string>(StringComparer.Ordinal);
                coverageByParty[signature.PartyId] = covered;
                firstSignatureByParty[signature.PartyId] = signature;
            }
            foreach (var scope in signature.Scope)
                covered.Add(scope);
        }

        foreach (var party in agreement.Parties)
        {
            coverageByParty.TryGetValue(party.Id, out var covered);
            var missing = signables.Where(s => covered is null || !covered.Contains(s)).ToList();
            if (missing.Count == 0) continue;

            string missingList = string.Join(", ", missing);
            if (firstSignatureByParty.TryGetValue(party.Id, out var signature))
            {
                issues.Add(new AuditIssue(
                    MediationIssueCodes.SignatureScopeGap,
                    Severity.Error,
                    $"Party '{party.Id}' has not signed: {missingList}.",
                    $"$.signatures[{signature.SourceIndex}].scope",
                    EvidenceScope.Signatures,
                    signature.SourceIndex,
                    -1,
                    "scope",
                    $"PTY:{party.Id}"));
            }
            else
            {
                // No signature at all for this party: anchor the evidence on the party entry.
                issues.Add(new AuditIssue(
                    MediationIssueCodes.SignatureScopeGap,
                    Severity.Error,
                    $"Party '{party.Id}' has no signature and has not signed: {missingList}.",
                    $"$.parties[{party.SourceIndex}].id",
                    EvidenceScope.Parties,
                    party.SourceIndex,
                    -1,
                    "id",
                    $"PTY:{party.Id}"));
            }
        }
    }

    private static void CheckWithdrawnReferenced(
        MediationAgreement agreement,
        Dictionary<string, Clause> clauseById,
        List<AuditIssue> issues)
    {
        var withdrawnAmendmentIds = new HashSet<string>(StringComparer.Ordinal);
        var withdrawnNewClauseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var amendment in agreement.Amendments)
        {
            if (!amendment.Withdrawn) continue;
            withdrawnAmendmentIds.Add(amendment.Id);
            if (!string.IsNullOrEmpty(amendment.NewClauseId))
                withdrawnNewClauseIds.Add(amendment.NewClauseId!);
        }

        if (withdrawnAmendmentIds.Count == 0) return;

        // A signature scope still covering a withdrawn amendment.
        foreach (var signature in agreement.Signatures)
        {
            for (int j = 0; j < signature.Scope.Count; j++)
            {
                if (withdrawnAmendmentIds.Contains(signature.Scope[j]))
                {
                    issues.Add(new AuditIssue(
                        MediationIssueCodes.WithdrawnAmendmentReferenced,
                        Severity.Error,
                        $"Signature by '{signature.PartyId}' still covers withdrawn amendment '{signature.Scope[j]}'.",
                        $"$.signatures[{signature.SourceIndex}].scope[{j}]",
                        EvidenceScope.Signatures,
                        signature.SourceIndex,
                        j,
                        "scope",
                        $"SIG:{signature.PartyId}\u0000{signature.Scope[j]}"));
                }
            }
        }

        // A live clause still referencing a clause id introduced by a withdrawn amendment.
        foreach (var clause in agreement.Clauses)
        {
            for (int j = 0; j < clause.References.Count; j++)
            {
                if (withdrawnNewClauseIds.Contains(clause.References[j]))
                {
                    issues.Add(new AuditIssue(
                        MediationIssueCodes.WithdrawnAmendmentReferenced,
                        Severity.Error,
                        $"Clause '{clause.Id}' references '{clause.References[j]}', which belongs to a withdrawn amendment.",
                        $"$.clauses[{clause.SourceIndex}].references[{j}]",
                        EvidenceScope.Clauses,
                        clause.SourceIndex,
                        j,
                        "references",
                        $"{clause.Id}\u0000{clause.References[j]}"));
                }
            }
        }
    }

    /// <summary>
    /// Audits the ordered event log. Three families of findings are emitted, all with evidence paths
    /// anchored on the raw <c>$.events[i]</c> entry and position-independent sort keys derived from the
    /// event sequence number:
    /// <list type="bullet">
    /// <item>MED_SIGNED_FACT_REVOCATION_ATTEMPT — a revoke-signed-fact event targets a party/scope that
    /// was actually signed (by a static signature or by an earlier-sequence sign event). History is
    /// preserved; only the attempt is reported.</item>
    /// <item>MED_EVENT_TARGET_UNKNOWN — an event names a party, amendment or agreement id absent from the package.</item>
    /// <item>MED_EVENT_SEQUENCE_CONFLICT — two or more events share the same sequence number.</item>
    /// </list>
    /// </summary>
    private static void CheckEvents(
        MediationAgreement agreement,
        List<AuditIssue> issues)
    {
        if (agreement.Events.Count == 0) return;

        var partyIds = new HashSet<string>(agreement.Parties.Select(p => p.Id), StringComparer.Ordinal);
        var amendmentIds = new HashSet<string>(agreement.Amendments.Select(a => a.Id), StringComparer.Ordinal);

        // The set of (party, scope) facts that are actually signed, considering both static signatures
        // and every sign event, is independent of event order — it is a union — so it is safe to build
        // up front and query for revoke-signed-fact attempts.
        var signedFacts = new HashSet<(string party, string scope)>();
        foreach (var signature in agreement.Signatures)
            foreach (var scope in signature.Scope)
                signedFacts.Add((signature.PartyId, scope));
        foreach (var evt in agreement.Events)
            if (evt.Type == AgreementEventType.Sign && evt.PartyId is not null && evt.Scope is not null)
                signedFacts.Add((evt.PartyId, evt.Scope));

        // Sequence conflicts: report every event that shares its sequence with another (the whole
        // colliding group), anchored per-event so evidence is precise.
        var bySequence = agreement.Events.GroupBy(e => e.Sequence);
        foreach (var group in bySequence)
        {
            if (group.Count() < 2) continue;
            foreach (var evt in group)
            {
                issues.Add(new AuditIssue(
                    MediationIssueCodes.EventSequenceConflict,
                    Severity.Error,
                    $"Event at sequence {evt.Sequence} shares its sequence number with another event, making their order ambiguous.",
                    $"$.events[{evt.SourceIndex}].seq",
                    EvidenceScope.Events,
                    evt.SourceIndex,
                    -1,
                    "seq",
                    $"{evt.Sequence:D19}\u0000{EventTieBreakKey(evt)}"));
            }
        }

        foreach (var evt in agreement.Events)
        {
            switch (evt.Type)
            {
                case AgreementEventType.RevokeSignedFact:
                    // Only a genuine attempt to erase an existing signed fact is reported; a
                    // revoke-signed-fact for something never signed is a plain unknown target.
                    if (evt.PartyId is not null && evt.Scope is not null
                        && signedFacts.Contains((evt.PartyId, evt.Scope)))
                    {
                        issues.Add(new AuditIssue(
                            MediationIssueCodes.SignedFactRevocationAttempt,
                            Severity.Error,
                            $"Event tries to revoke the recorded fact that '{evt.PartyId}' signed '{evt.Scope}'; signing history is immutable and is preserved.",
                            $"$.events[{evt.SourceIndex}]",
                            EvidenceScope.Events,
                            evt.SourceIndex,
                            -1,
                            "",
                            $"{evt.PartyId}\u0000{evt.Scope}\u0000{evt.Sequence:D19}"));
                    }
                    break;
            }

            // Unknown-target detection for every event kind that names an id.
            string? unknownDetail = null;
            if (evt.PartyId is not null
                && (evt.Type is AgreementEventType.Sign or AgreementEventType.RemoveParty
                    or AgreementEventType.RevokeSignedFact)
                && !partyIds.Contains(evt.PartyId))
            {
                unknownDetail = $"party '{evt.PartyId}'";
            }
            else if (evt.AmendmentId is not null
                && evt.Type == AgreementEventType.RevokeAmendment
                && !amendmentIds.Contains(evt.AmendmentId))
            {
                unknownDetail = $"amendment '{evt.AmendmentId}'";
            }

            if (unknownDetail is not null)
            {
                issues.Add(new AuditIssue(
                    MediationIssueCodes.EventTargetUnknown,
                    Severity.Error,
                    $"Event references {unknownDetail}, which does not exist in the package.",
                    $"$.events[{evt.SourceIndex}]",
                    EvidenceScope.Events,
                    evt.SourceIndex,
                    -1,
                    "",
                    $"{evt.Sequence:D19}\u0000{EventTieBreakKey(evt)}"));
            }
        }
    }

    // ----- cycle detection (iterative Tarjan SCC) ----------------------------------------------

    private static void CheckReferenceCycles(
        MediationAgreement agreement,
        Dictionary<string, Clause> clauseById,
        List<AuditIssue> issues)
    {
        // Edges point from a clause to each existing clause it references. Cycle detection uses the
        // iterative SCC helper so arbitrarily deep chains never touch the call stack.
        var cycles = StronglyConnectedComponents.FindCycles(
            nodeIds: clauseById.Keys,
            edgesFrom: node => clauseById.TryGetValue(node, out var clause)
                ? clause.References.Where(clauseById.ContainsKey)
                : Enumerable.Empty<string>());

        foreach (var component in cycles)
        {
            // Canonical representative: the lowest clause id in the cycle, so the emitted issue and
            // its shortest evidence path are independent of the order SCCs were discovered in.
            var members = component.OrderBy(id => id, StringComparer.Ordinal).ToList();
            var repClause = clauseById[members[0]];

            issues.Add(new AuditIssue(
                MediationIssueCodes.ReferenceCycle,
                Severity.Error,
                $"Clause reference cycle detected among: {string.Join(", ", members)}.",
                $"$.clauses[{repClause.SourceIndex}].references",
                EvidenceScope.Clauses,
                repClause.SourceIndex,
                -1,
                "references",
                $"{repClause.Id}"));
        }
    }

    // ----- deterministic ordering --------------------------------------------------------------

    /// <summary>
    /// Orders issues purely by their stable identity: the issue code first, then the
    /// position-independent <see cref="AuditIssue.SortKey"/>. Neither physical array indices,
    /// evidence-path text, nor the display message are consulted, so swapping the order of the
    /// <c>$.amendments</c> array (or any other reordering of the same package) yields an identical
    /// issue sequence.
    /// </summary>
    private static int CompareIssues(AuditIssue a, AuditIssue b)
    {
        int c = string.CompareOrdinal(a.Code, b.Code);
        if (c != 0) return c;
        return string.CompareOrdinal(a.SortKey, b.SortKey);
    }

    // ----- JSON helpers ------------------------------------------------------------------------

    private static IEnumerable<(JsonElement element, int index)> EnumerateArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            yield break;

        int index = 0;
        foreach (var element in array.EnumerateArray())
            yield return (element, index++);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var result)
            ? result
            : null;

    private static bool? GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static DateTimeOffset? GetDate(JsonElement element, string name)
    {
        var raw = GetString(element, name);
        if (raw is null) return null;
        return DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                result.Add(item.GetString()!);
        return result;
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Iterative strongly-connected-components (Tarjan) used for every cycle check. The algorithm keeps
/// its own explicit work stack, so it detects cycles in graphs with arbitrarily deep chains without
/// ever risking a stack overflow. Node identity is a string id; the caller supplies the successor
/// function. Results are deterministic: nodes are processed in ordinal id order.
/// </summary>
internal static class StronglyConnectedComponents
{
    /// <summary>
    /// Returns every non-trivial cycle (each strongly connected component of size &gt; 1, plus any
    /// single node that references itself). Each cycle is returned as the set of node ids it contains.
    /// </summary>
    /// <param name="nodeIds">All node ids in the graph.</param>
    /// <param name="edgesFrom">Maps a node id to the ids it has directed edges to.</param>
    /// <returns>One list of node ids per detected cycle.</returns>
    public static List<List<string>> FindCycles(
        IEnumerable<string> nodeIds,
        Func<string, IEnumerable<string>> edgesFrom)
    {
        // Index nodes deterministically by ordinal id order.
        var ids = nodeIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
        int n = ids.Count;
        var idToNode = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++) idToNode[ids[i]] = i;

        var adjacency = new List<int>[n];
        var selfLoop = new bool[n];
        for (int i = 0; i < n; i++)
        {
            adjacency[i] = new List<int>();
            foreach (var successor in edgesFrom(ids[i]))
            {
                if (idToNode.TryGetValue(successor, out int target))
                {
                    if (target == i) selfLoop[i] = true;
                    adjacency[i].Add(target);
                }
            }
        }

        var indexOf = new int[n];
        var lowLink = new int[n];
        var onStack = new bool[n];
        var visited = new bool[n];
        for (int i = 0; i < n; i++) indexOf[i] = -1;

        var sccStack = new Stack<int>();
        int nextIndex = 0;
        var components = new List<List<int>>();

        for (int start = 0; start < n; start++)
        {
            if (visited[start]) continue;

            // Each work-stack frame tracks a node and the next adjacency slot to explore.
            var work = new Stack<(int node, int next)>();
            work.Push((start, 0));

            while (work.Count > 0)
            {
                var (v, next) = work.Pop();

                if (next == 0)
                {
                    visited[v] = true;
                    indexOf[v] = lowLink[v] = nextIndex++;
                    sccStack.Push(v);
                    onStack[v] = true;
                }

                bool pushedChild = false;
                for (int k = next; k < adjacency[v].Count; k++)
                {
                    int w = adjacency[v][k];
                    if (indexOf[w] == -1)
                    {
                        // Resume v at k+1 after w is fully processed, then descend into w.
                        work.Push((v, k + 1));
                        work.Push((w, 0));
                        pushedChild = true;
                        break;
                    }
                    else if (onStack[w])
                    {
                        if (indexOf[w] < lowLink[v]) lowLink[v] = indexOf[w];
                    }
                }

                if (pushedChild) continue;

                // v is fully explored: close a component at its root and propagate to the parent.
                if (lowLink[v] == indexOf[v])
                {
                    var component = new List<int>();
                    int w;
                    do
                    {
                        w = sccStack.Pop();
                        onStack[w] = false;
                        component.Add(w);
                    } while (w != v);
                    components.Add(component);
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek();
                    if (lowLink[v] < lowLink[parent.node]) lowLink[parent.node] = lowLink[v];
                }
            }
        }

        var cycles = new List<List<string>>();
        foreach (var component in components)
        {
            bool isCycle = component.Count > 1 || (component.Count == 1 && selfLoop[component[0]]);
            if (!isCycle) continue;
            cycles.Add(component.Select(node => ids[node]).ToList());
        }
        return cycles;
    }
}

/// <summary>
/// Console entry point: reads an agreement package (from a path argument or the bundled fixture)
/// and prints each finding's stable code, severity, evidence path and message.
/// </summary>
public static class Program
{
    /// <summary>Program entry point.</summary>
    /// <param name="args">Optional single argument: path to an agreement JSON package.</param>
    /// <returns>0 when no errors were found; 1 when at least one error-severity issue exists; 2 on parse failure.</returns>
    public static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : DefaultFixturePath();

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Unable to read agreement package '{path}': {ex.Message}");
            return 2;
        }

        var auditor = new AgreementAuditor();
        MediationAgreement agreement;
        try
        {
            agreement = AgreementAuditor.Parse(json);
        }
        catch (AgreementParseException ex)
        {
            Console.Error.WriteLine($"Failed to parse agreement package: {ex.Message}");
            return 2;
        }

        var chain = auditor.BuildVersionChain(agreement);
        AuditResult result = auditor.Audit(agreement);

        Console.WriteLine($"Audited package: {path}");
        Console.WriteLine();

        Console.WriteLine("Version chain (revision edges, ordered by amendment id):");
        if (chain.Edges.Count == 0)
        {
            Console.WriteLine("  (no amendments)");
        }
        else
        {
            foreach (var edge in chain.Edges)
            {
                string arrow = edge.Kind == RevisionKind.Replacement
                    ? $"{edge.FromClauseId} -> {edge.ToClauseId}"
                    : $"(append) {edge.ToClauseId}";
                Console.WriteLine($"  {edge.AmendmentId}: {arrow}  [{edge.EvidencePath}]");
            }
        }
        Console.WriteLine();
        Console.WriteLine($"Effective clause versions: {string.Join(", ", chain.EffectiveClauseIds())}");
        Console.WriteLine();

        var state = auditor.ComputeEffectiveState(agreement);
        if (agreement.Events.Count > 0)
        {
            Console.WriteLine("Effective state after folding events (by sequence):");
            Console.WriteLine($"  agreement revoked : {state.AgreementRevoked}");
            Console.WriteLine($"  active parties    : {string.Join(", ", state.ActiveParties)}");
            Console.WriteLine($"  revoked amendments: {(state.RevokedAmendmentIds.Count == 0 ? "(none)" : string.Join(", ", state.RevokedAmendmentIds))}");
            foreach (var kv in state.EffectiveSignatures)
                Console.WriteLine($"  signed [{kv.Key}]    : {string.Join(", ", kv.Value)}");
            Console.WriteLine();
        }

        Console.WriteLine($"Issues found: {result.Issues.Count}");
        Console.WriteLine();

        if (result.Issues.Count == 0)
        {
            Console.WriteLine("No consistency problems detected.");
            return 0;
        }

        foreach (var issue in result.Issues)
        {
            Console.WriteLine($"{issue.Code} [{issue.Severity}]");
            Console.WriteLine($"  evidence : {issue.EvidencePath}");
            Console.WriteLine($"  detail   : {issue.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("Counts by code:");
        foreach (var pair in result.CountByCode())
            Console.WriteLine($"  {pair.Key}: {pair.Value}");

        return result.HasErrors ? 1 : 0;
    }

    private static string DefaultFixturePath()
    {
        // Walk up from the executing assembly location to find the repo's materials fixture.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "materials", "agreements.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine("materials", "agreements.json");
    }
}
