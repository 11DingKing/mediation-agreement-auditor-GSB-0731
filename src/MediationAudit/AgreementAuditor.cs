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

    /// <summary>A withdrawn amendment is still referenced by a signature scope or a live clause.</summary>
    public const string WithdrawnAmendmentReferenced = "MED_WITHDRAWN_AMENDMENT_REFERENCED";
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
/// The effective obligation that results after live amendments are applied to a clause lineage.
/// </summary>
/// <param name="ClauseId">Effective clause id carrying the obligation.</param>
/// <param name="Obligor">Party id that owes the obligation.</param>
/// <param name="AmountFen">Effective amount in fen, if known.</param>
/// <param name="Due">Effective due date, if known.</param>
public sealed record Obligation(string ClauseId, string? Obligor, long? AmountFen, DateTimeOffset? Due);

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

    /// <summary>Initializes a new <see cref="MediationAgreement"/>.</summary>
    /// <param name="agreementId">The agreement identifier.</param>
    /// <param name="parties">Parties in source order.</param>
    /// <param name="clauses">Clauses in source order.</param>
    /// <param name="amendments">Amendments in source order.</param>
    /// <param name="signatures">Signatures in source order.</param>
    public MediationAgreement(
        string agreementId,
        IReadOnlyList<Party> parties,
        IReadOnlyList<Clause> clauses,
        IReadOnlyList<Amendment> amendments,
        IReadOnlyList<Signature> signatures)
    {
        AgreementId = agreementId;
        Parties = parties;
        Clauses = clauses;
        Amendments = amendments;
        Signatures = signatures;
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
        string field)
    {
        Code = code;
        Severity = severity;
        Message = message;
        EvidencePath = evidencePath;
        Scope = scope;
        PrimaryIndex = primaryIndex;
        SecondaryIndex = secondaryIndex;
        Field = field;
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

            return new MediationAgreement(agreementId, parties, clauses, amendments, signatures);
        }
    }

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

        // Map each replaced clause id to the winning live amendment (lowest source index wins so the
        // result is independent of amendment ordering in the file).
        var winningAmendment = new Dictionary<string, Amendment>(StringComparer.Ordinal);
        foreach (var amendment in agreement.Amendments.OrderBy(a => a.SourceIndex))
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
        CheckUnknownReferences(agreement, knownClauseIds, issues);
        CheckReferenceCycles(agreement, clauseById, issues);
        CheckDateOrder(agreement, clauseById, issues);
        CheckSignatureScope(agreement, validLiveAmendments, issues);
        CheckWithdrawnReferenced(agreement, clauseById, issues);

        issues.Sort(CompareIssues);
        return new AuditResult(issues);
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
                    "replaces"));
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
            var ordered = group.OrderBy(a => a.SourceIndex).ToList();
            if (ordered.Count < 2) continue;

            var baseline = ordered[0];
            // Every amendment after the earliest one is a structurally ambiguous replacement.
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
                    "replaces"));

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
                        "amountFen"));
                }
            }
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
                        "references"));
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
                        "references"));
                }
            }
        }
    }

    private static void CheckSignatureScope(
        MediationAgreement agreement,
        List<Amendment> validLiveAmendments,
        List<AuditIssue> issues)
    {
        // Signable ids: the base agreement plus every valid live amendment.
        var signables = new List<string> { agreement.AgreementId };
        signables.AddRange(validLiveAmendments.Select(a => a.Id));

        // First signature per party (lowest source index) carries the coverage evidence.
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
                    "scope"));
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
                    "id"));
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
                        "scope"));
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
                        "references"));
                }
            }
        }
    }

    // ----- cycle detection (iterative Tarjan SCC) ----------------------------------------------

    private static void CheckReferenceCycles(
        MediationAgreement agreement,
        Dictionary<string, Clause> clauseById,
        List<AuditIssue> issues)
    {
        // Build the directed graph over existing clause ids only. Nodes are indexed by their
        // position in a stable id-sorted list so the algorithm is deterministic.
        var nodeIds = clauseById.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        int n = nodeIds.Count;
        var idToNode = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++) idToNode[nodeIds[i]] = i;

        var adjacency = new List<int>[n];
        var selfLoop = new bool[n];
        for (int i = 0; i < n; i++)
        {
            adjacency[i] = new List<int>();
            var clause = clauseById[nodeIds[i]];
            foreach (var reference in clause.References)
            {
                if (idToNode.TryGetValue(reference, out int target))
                {
                    if (target == i) selfLoop[i] = true;
                    adjacency[i].Add(target);
                }
            }
        }

        // Iterative Tarjan's strongly-connected-components algorithm: an explicit work stack keeps
        // arbitrarily deep reference chains off the call stack.
        var indexOf = new int[n];
        var lowLink = new int[n];
        var onStack = new bool[n];
        var visited = new bool[n];
        for (int i = 0; i < n; i++) indexOf[i] = -1;

        var sccStack = new Stack<int>();
        int nextIndex = 0;

        var sccs = new List<List<int>>();

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

                // v is fully explored: propagate low-links to the parent and close SCCs at roots.
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
                    sccs.Add(component);
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek();
                    if (lowLink[v] < lowLink[parent.node]) lowLink[parent.node] = lowLink[v];
                }
            }
        }

        foreach (var component in sccs)
        {
            bool isCycle = component.Count > 1 || (component.Count == 1 && selfLoop[component[0]]);
            if (!isCycle) continue;

            // Canonical representative: the lowest clause id in the cycle, so the emitted issue is
            // independent of the order SCCs were discovered in.
            int repNode = component.OrderBy(node => nodeIds[node], StringComparer.Ordinal).First();
            var members = component.Select(node => nodeIds[node]).OrderBy(id => id, StringComparer.Ordinal);
            var repClause = clauseById[nodeIds[repNode]];

            issues.Add(new AuditIssue(
                MediationIssueCodes.ReferenceCycle,
                Severity.Error,
                $"Clause reference cycle detected among: {string.Join(", ", members)}.",
                $"$.clauses[{repClause.SourceIndex}].references",
                EvidenceScope.Clauses,
                repClause.SourceIndex,
                -1,
                "references"));
        }
    }

    // ----- deterministic ordering --------------------------------------------------------------

    /// <summary>
    /// Orders issues purely by their stable identity: issue code, then evidence scope, primary
    /// index, secondary index and field. Message text and discovery/traversal order are never
    /// consulted, so the ordering is reproducible for identical (even reordered) inputs.
    /// </summary>
    private static int CompareIssues(AuditIssue a, AuditIssue b)
    {
        int c = string.CompareOrdinal(a.Code, b.Code);
        if (c != 0) return c;
        c = ((int)a.Scope).CompareTo((int)b.Scope);
        if (c != 0) return c;
        c = a.PrimaryIndex.CompareTo(b.PrimaryIndex);
        if (c != 0) return c;
        c = a.SecondaryIndex.CompareTo(b.SecondaryIndex);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Field, b.Field);
        if (c != 0) return c;
        // Final tie-breaker keeps ordering total even if two findings share a location.
        return string.CompareOrdinal(a.Message, b.Message);
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

        AuditResult result;
        try
        {
            result = new AgreementAuditor().AuditJson(json);
        }
        catch (AgreementParseException ex)
        {
            Console.Error.WriteLine($"Failed to parse agreement package: {ex.Message}");
            return 2;
        }

        Console.WriteLine($"Audited package: {path}");
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
