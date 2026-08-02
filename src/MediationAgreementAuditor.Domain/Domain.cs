using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediationAgreementAuditor;

/// <summary>
/// Severity of an audit issue.
/// </summary>
public enum Severity
{
    /// <summary>The agreement is inconsistent and must be fixed before execution.</summary>
    Error = 0,

    /// <summary>A suspicious condition that should be reviewed by staff.</summary>
    Warning = 1,

    /// <summary>An informational notice that does not block execution.</summary>
    Info = 2
}

/// <summary>
/// A single, stable audit finding. The <see cref="Code"/> is invariant across text
/// and traversal-order changes; only the evidence path locates the originating JSON.
/// </summary>
/// <param name="Code">Stable issue code with the <c>MED_</c> prefix.</param>
/// <param name="Severity">Severity of the finding.</param>
/// <param name="Message">Human-readable explanation (never used for sorting or identity).</param>
/// <param name="EvidencePath">JSONPath into the original package, e.g. <c>$.clauses[0].references[1]</c>.</param>
public sealed record Issue(
    string Code,
    Severity Severity,
    string Message,
    string EvidencePath);

/// <summary>
/// A party to the mediation agreement.
/// </summary>
public sealed class Party
{
    /// <summary>Stable identifier of the party.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name of the party.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// A clause defining an obligation from one obligor toward the other parties.
/// </summary>
public sealed class Clause
{
    /// <summary>Stable identifier of the clause.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Identifier of the party that owes the obligation.</summary>
    public string Obligor { get; set; } = string.Empty;

    /// <summary>Amount in fen (1/100 of a yuan), or <see langword="null"/> when unspecified.</summary>
    public long? AmountFen { get; set; }

    /// <summary>Due date in <c>yyyy-MM-dd</c> form, or <see langword="null"/> when unspecified.</summary>
    public string? Due { get; set; }

    /// <summary>Identifiers of clauses this clause depends on.</summary>
    public List<string> References { get; set; } = new();
}

/// <summary>
/// A supplementary agreement that replaces a clause version or withdraws an earlier amendment.
/// </summary>
public sealed class Amendment
{
    /// <summary>Stable identifier of the amendment.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Identifier of the clause or clause version being replaced.</summary>
    public string? Replaces { get; set; }

    /// <summary>Identifier of the new clause version introduced by this amendment.</summary>
    public string? NewClauseId { get; set; }

    /// <summary>Replacement amount in fen, or <see langword="null"/> to inherit.</summary>
    public long? AmountFen { get; set; }

    /// <summary>Replacement due date in <c>yyyy-MM-dd</c> form, or <see langword="null"/> to inherit.</summary>
    public string? Due { get; set; }

    /// <summary>Identifier of an amendment this amendment withdraws, or <see langword="null"/>.</summary>
    public string? Withdraws { get; set; }
}

/// <summary>
/// A party's signature covering the agreement and selected amendments.
/// </summary>
public sealed class Signature
{
    /// <summary>Identifier of the signing party.</summary>
    public string PartyId { get; set; } = string.Empty;

    /// <summary>Identifiers of the agreement and amendments covered by the signature.</summary>
    public List<string> Scope { get; set; } = new();

    /// <summary>ISO-8601 instant the signature was applied.</summary>
    public string? SignedAt { get; set; }
}

/// <summary>
/// The full mediation agreement package loaded from JSON.
/// </summary>
public sealed class AgreementPackage
{
    /// <summary>Identifier of the master agreement.</summary>
    public string AgreementId { get; set; } = string.Empty;

    /// <summary>Parties to the agreement.</summary>
    public List<Party> Parties { get; set; } = new();

    /// <summary>Original clauses of the agreement.</summary>
    public List<Clause> Clauses { get; set; } = new();

    /// <summary>Supplementary amendments that replace or withdraw prior content.</summary>
    public List<Amendment> Amendments { get; set; } = new();

    /// <summary>Signatures collected from the parties.</summary>
    public List<Signature> Signatures { get; set; } = new();
}

/// <summary>
/// An effective obligation resolved from a clause and its active amendment chain.
/// </summary>
public sealed class Obligation
{
    /// <summary>Identifier of the clause version currently in force.</summary>
    public string ClauseVersionId { get; init; } = string.Empty;

    /// <summary>Identifier of the party owing the obligation.</summary>
    public string ObligorPartyId { get; init; } = string.Empty;

    /// <summary>Effective amount in fen, or <see langword="null"/> when not defined.</summary>
    public long? AmountFen { get; init; }

    /// <summary>Effective due date, or <see langword="null"/> when not defined.</summary>
    public DateOnly? Due { get; init; }

    /// <summary>JSONPath of the field that defines the effective amount.</summary>
    public string AmountEvidencePath { get; init; } = string.Empty;

    /// <summary>JSONPath of the field that defines the effective due date.</summary>
    public string DueEvidencePath { get; init; } = string.Empty;
}

/// <summary>
/// Result of auditing an <see cref="AgreementPackage"/>.
/// </summary>
public sealed class AuditResult
{
    /// <summary>Identifier of the audited agreement.</summary>
    public string AgreementId { get; init; } = string.Empty;

    /// <summary>
    /// All findings sorted deterministically by code, then evidence path, then message.
    /// </summary>
    public IReadOnlyList<Issue> Issues { get; init; } = Array.Empty<Issue>();

    /// <summary>Effective obligations keyed by original clause identifier.</summary>
    public IReadOnlyDictionary<string, Obligation> EffectiveObligations { get; init; } =
        new Dictionary<string, Obligation>();

    /// <summary><see langword="true"/> when at least one error-severity issue exists.</summary>
    public bool HasErrors { get; init; }
}

/// <summary>
/// Stable issue codes emitted by <see cref="AgreementAuditor"/>. Codes never depend on
/// message text or traversal order; they are safe to use as regression anchors.
/// </summary>
public static class IssueCodes
{
    /// <summary>A party identifier is declared more than once.</summary>
    public const string PartyDuplicate = "MED_PARTY_DUPLICATE";

    /// <summary>A clause names an obligor that is not a declared party.</summary>
    public const string PartyObligorUnknown = "MED_PARTY_OBLIGOR_UNKNOWN";

    /// <summary>A clause identifier is declared more than once.</summary>
    public const string ClauseDuplicate = "MED_CLAUSE_DUPLICATE";

    /// <summary>A clause references an identifier that resolves to no clause or active version.</summary>
    public const string RefDangling = "MED_REF_DANGLING";

    /// <summary>The clause reference graph contains a directed cycle.</summary>
    public const string RefCycle = "MED_REF_CYCLE";

    /// <summary>An amendment identifier is declared more than once.</summary>
    public const string AmendmentDuplicate = "MED_AMD_DUPLICATE";

    /// <summary>An amendment is structurally invalid (missing target or new version).</summary>
    public const string AmendmentMalformed = "MED_AMD_MALFORMED";

    /// <summary>An amendment replaces a clause or version that does not exist.</summary>
    public const string AmendmentTargetMissing = "MED_AMD_TARGET_MISSING";

    /// <summary>An amendment's new version identifier collides with an existing one.</summary>
    public const string AmendmentNewIdCollision = "MED_AMD_NEW_ID_COLLISION";

    /// <summary>Multiple active amendments compete for the same target, or a replacement chain cycles.</summary>
    public const string AmendmentAmbiguous = "MED_AMD_AMBIGUOUS";

    /// <summary>An amendment withdraws an amendment identifier that does not exist.</summary>
    public const string AmendmentWithdrawUnknown = "MED_AMD_WITHDRAW_UNKNOWN";

    /// <summary>Amendment withdrawals form a directed cycle.</summary>
    public const string AmendmentWithdrawCycle = "MED_AMD_WITHDRAW_CYCLE";

    /// <summary>A signature belongs to a party that is not declared.</summary>
    public const string SignaturePartyUnknown = "MED_SIG_PARTY_UNKNOWN";

    /// <summary>A signature scope references an unknown agreement or amendment identifier.</summary>
    public const string SignatureScopeUnknown = "MED_SIG_SCOPE_UNKNOWN";

    /// <summary>A signature covers an amendment that has been withdrawn.</summary>
    public const string SignatureScopeWithdrawn = "MED_SIG_SCOPE_WITHDRAWN";

    /// <summary>A declared party has not signed the master agreement.</summary>
    public const string SignatureMissing = "MED_SIG_MISSING";

    /// <summary>An active amendment affecting an obligor is not signed by that obligor.</summary>
    public const string SignatureAmdMissing = "MED_SIG_AMD_MISSING";

    /// <summary>A declared amount is negative.</summary>
    public const string AmountNegative = "MED_AMOUNT_NEGATIVE";

    /// <summary>A clause amount disagrees with the sum of its referenced clause amounts.</summary>
    public const string AmountSumMismatch = "MED_AMOUNT_SUM_MISMATCH";

    /// <summary>A date string cannot be parsed.</summary>
    public const string DateInvalid = "MED_DATE_INVALID";

    /// <summary>A clause is due before a clause it depends on.</summary>
    public const string DateBeforeReferenced = "MED_DATE_BEFORE_REFERENCED";
}

/// <summary>
/// Audits mediation agreement packages for cross-clause consistency. The auditor is a
/// pure function of the input package: duplicate submissions produce byte-identical
/// results, and all graph traversal is iterative to avoid stack overflows on malicious input.
/// </summary>
public static class AgreementAuditor
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnknownTypeHandling = JsonUnknownTypeHandling.JsonNode,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    /// <summary>
    /// Loads a package from a JSON file and audits it.
    /// </summary>
    /// <param name="path">Path to the agreement JSON file.</param>
    /// <returns>A deterministic <see cref="AuditResult"/>.</returns>
    public static AuditResult AuditFile(string path)
    {
        var json = File.ReadAllText(path);
        return AuditJson(json);
    }

    /// <summary>
    /// Parses a JSON string and audits the resulting package.
    /// </summary>
    /// <param name="json">The agreement package as JSON.</param>
    /// <returns>A deterministic <see cref="AuditResult"/>.</returns>
    public static AuditResult AuditJson(string json)
    {
        var package = JsonSerializer.Deserialize<AgreementPackage>(json, ReadOptions)
                      ?? new AgreementPackage();
        return Audit(package);
    }

    /// <summary>
    /// Audits an already-deserialized package. Array positions in the package are treated
    /// as the original JSON array indices for evidence paths.
    /// </summary>
    /// <param name="package">The agreement package to audit.</param>
    /// <returns>A deterministic <see cref="AuditResult"/>.</returns>
    public static AuditResult Audit(AgreementPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var issues = new List<Issue>();
        var p = package;

        var partyIds = new HashSet<string>(StringComparer.Ordinal);
        var partyOrder = new List<string>();
        for (var i = 0; i < p.Parties.Count; i++)
        {
            var id = p.Parties[i].Id ?? string.Empty;
            if (!partyIds.Add(id))
            {
                issues.Add(new Issue(IssueCodes.PartyDuplicate, Severity.Error,
                    $"Duplicate party identifier '{id}'.",
                    $"$.parties[{i}].id"));
            }
            else
            {
                partyOrder.Add(id);
            }
        }

        var clauseIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < p.Clauses.Count; i++)
        {
            var id = p.Clauses[i].Id ?? string.Empty;
            if (!clauseIds.Add(id))
            {
                issues.Add(new Issue(IssueCodes.ClauseDuplicate, Severity.Error,
                    $"Duplicate clause identifier '{id}'.",
                    $"$.clauses[{i}].id"));
            }

            var obligor = p.Clauses[i].Obligor ?? string.Empty;
            if (obligor.Length > 0 && !partyIds.Contains(obligor))
            {
                issues.Add(new Issue(IssueCodes.PartyObligorUnknown, Severity.Error,
                    $"Clause '{id}' obligor '{obligor}' is not a declared party.",
                    $"$.clauses[{i}].obligor"));
            }
        }

        var amendmentById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < p.Amendments.Count; i++)
        {
            var id = p.Amendments[i].Id ?? string.Empty;
            if (!amendmentById.TryAdd(id, i))
            {
                issues.Add(new Issue(IssueCodes.AmendmentDuplicate, Severity.Error,
                    $"Duplicate amendment identifier '{id}'.",
                    $"$.amendments[{i}].id"));
            }
        }

        var n = p.Amendments.Count;
        var withdrawEdge = new int?[n];
        var hasReplacement = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var a = p.Amendments[i];
            var hasReplaces = !string.IsNullOrEmpty(a.Replaces);
            var hasNewId = !string.IsNullOrEmpty(a.NewClauseId);
            var hasWithdraws = !string.IsNullOrEmpty(a.Withdraws);

            if (hasReplaces != hasNewId || (!hasReplaces && !hasWithdraws))
            {
                issues.Add(new Issue(IssueCodes.AmendmentMalformed, Severity.Error,
                    $"Amendment '{a.Id}' must either replace a clause (replaces + newClauseId) or withdraw an amendment.",
                    $"$.amendments[{i}]"));
            }
            else
            {
                hasReplacement[i] = hasReplaces;
            }

            if (hasWithdraws)
            {
                if (amendmentById.TryGetValue(a.Withdraws!, out var target))
                {
                    if (target == i)
                    {
                        issues.Add(new Issue(IssueCodes.AmendmentWithdrawCycle, Severity.Error,
                            $"Amendment '{a.Id}' withdraws itself.",
                            $"$.amendments[{i}].withdraws"));
                    }
                    else
                    {
                        withdrawEdge[i] = target;
                    }
                }
                else
                {
                    issues.Add(new Issue(IssueCodes.AmendmentWithdrawUnknown, Severity.Error,
                        $"Amendment '{a.Id}' withdraws unknown amendment '{a.Withdraws}'.",
                        $"$.amendments[{i}].withdraws"));
                }
            }
        }

        var withdrawn = ResolveWithdrawnAmendments(n, withdrawEdge, issues, p);
        var active = new bool[n];
        for (var i = 0; i < n; i++)
        {
            active[i] = hasReplacement[i] && !withdrawn[i];
        }

        var newIdOwner = new Dictionary<string, int>(StringComparer.Ordinal);
        var targetSuccessors = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var activeNewIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < n; i++)
        {
            if (!active[i])
            {
                continue;
            }

            var a = p.Amendments[i];
            var newId = a.NewClauseId!;
            activeNewIds.Add(newId);

            if (clauseIds.Contains(newId) || !newIdOwner.TryAdd(newId, i))
            {
                issues.Add(new Issue(IssueCodes.AmendmentNewIdCollision, Severity.Error,
                    $"Amendment '{a.Id}' newClauseId '{newId}' collides with an existing identifier.",
                    $"$.amendments[{i}].newClauseId"));
            }

            var target = a.Replaces!;
            if (!targetSuccessors.TryGetValue(target, out var list))
            {
                list = new List<int>();
                targetSuccessors[target] = list;
            }
            list.Add(i);
        }

        for (var i = 0; i < n; i++)
        {
            if (!active[i])
            {
                continue;
            }

            var a = p.Amendments[i];
            var target = a.Replaces!;
            var targetIsOriginal = clauseIds.Contains(target);
            var targetIsActiveVersion = activeNewIds.Contains(target);
            if (!targetIsOriginal && !targetIsActiveVersion)
            {
                issues.Add(new Issue(IssueCodes.AmendmentTargetMissing, Severity.Error,
                    $"Amendment '{a.Id}' replaces unknown clause or version '{target}'.",
                    $"$.amendments[{i}].replaces"));
            }

            if (targetSuccessors.TryGetValue(target, out var succ) && succ.Count > 1)
            {
                // Reported once per competing amendment below to keep evidence precise.
            }
        }

        foreach (var kvp in targetSuccessors)
        {
            if (kvp.Value.Count > 1)
            {
                foreach (var idx in kvp.Value)
                {
                    issues.Add(new Issue(IssueCodes.AmendmentAmbiguous, Severity.Error,
                        $"Amendment '{p.Amendments[idx].Id}' is one of {kvp.Value.Count} active amendments replacing '{kvp.Key}'.",
                        $"$.amendments[{idx}].replaces"));
                }
            }
        }

        var firstSuccessor = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kvp in targetSuccessors)
        {
            var min = kvp.Value[0];
            foreach (var idx in kvp.Value)
            {
                if (idx < min)
                {
                    min = idx;
                }
            }
            firstSuccessor[kvp.Key] = min;
        }

        var successorVersion = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in firstSuccessor)
        {
            successorVersion[kvp.Key] = p.Amendments[kvp.Value].NewClauseId!;
        }

        var versionValues = BuildVersionValues(
            p, active, clauseIds, activeNewIds, targetSuccessors, newIdOwner, issues);

        BuildReferenceGraph(p, clauseIds, activeNewIds, out var nodeIndex,
            out var nodeId, out var adjacency, out var edgeEvidence, issues);
        DetectReferenceCycles(nodeIndex.Count, adjacency, edgeEvidence, nodeId, p, issues);

        CheckAmountsAndDates(p, versionValues, successorVersion, issues);

        CheckSignatures(p, partyIds, partyOrder, clauseIds, amendmentById, active,
            activeNewIds, targetSuccessors, versionValues, issues);

        issues.Sort(CompareIssues);

        var effective = BuildEffectiveObligations(p, successorVersion, versionValues);
        var hasErrors = issues.Exists(i => i.Severity == Severity.Error);

        return new AuditResult
        {
            AgreementId = p.AgreementId ?? string.Empty,
            Issues = issues,
            EffectiveObligations = effective,
            HasErrors = hasErrors
        };
    }

    private static bool[] ResolveWithdrawnAmendments(
        int n, int?[] withdrawEdge, List<Issue> issues, AgreementPackage p)
    {
        var state = new int[n];
        var removed = new bool[n];

        for (var start = 0; start < n; start++)
        {
            if (state[start] != 0 || !withdrawEdge[start].HasValue)
            {
                if (state[start] == 0)
                {
                    state[start] = 2;
                }
                continue;
            }

            var path = new List<int>();
            var pos = new Dictionary<int, int>();
            var cur = start;
            while (true)
            {
                if (state[cur] == 2 || removed[cur] || !withdrawEdge[cur].HasValue)
                {
                    break;
                }

                if (state[cur] == 1)
                {
                    var cycleStart = pos[cur];
                    var cycle = path.Skip(cycleStart).ToList();
                    var worst = cycle[0];
                    foreach (var node in cycle)
                    {
                        if (node > worst)
                        {
                            worst = node;
                        }
                    }

                    var pathText = string.Join(" -> ", cycle.ConvertAll(i => p.Amendments[i].Id));
                    issues.Add(new Issue(IssueCodes.AmendmentWithdrawCycle, Severity.Error,
                        $"Withdrawal cycle detected: {pathText} -> {p.Amendments[cur].Id}.",
                        $"$.amendments[{cycle[0]}].withdraws"));
                    withdrawEdge[worst] = null;
                    removed[worst] = true;
                    break;
                }

                state[cur] = 1;
                pos[cur] = path.Count;
                path.Add(cur);
                cur = withdrawEdge[cur]!.Value;
            }

            foreach (var node in path)
            {
                state[node] = 2;
            }
        }

        var inDegree = new int[n];
        var outgoing = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            outgoing[i] = new List<int>();
        }

        for (var i = 0; i < n; i++)
        {
            if (withdrawEdge[i].HasValue)
            {
                var t = withdrawEdge[i]!.Value;
                outgoing[i].Add(t);
                inDegree[t]++;
            }
        }

        var pq = new PriorityQueue<int, int>();
        for (var i = 0; i < n; i++)
        {
            if (inDegree[i] == 0)
            {
                pq.Enqueue(i, i);
            }
        }

        var withdrawn = new bool[n];
        while (pq.TryDequeue(out var u, out _))
        {
            foreach (var v in outgoing[u])
            {
                if (!withdrawn[u])
                {
                    withdrawn[v] = true;
                }
                inDegree[v]--;
                if (inDegree[v] == 0)
                {
                    pq.Enqueue(v, v);
                }
            }
        }

        return withdrawn;
    }

    private sealed class VersionValue
    {
        public long? AmountFen { get; init; }
        public DateOnly? Due { get; init; }
        public string AmountEvidencePath { get; init; } = string.Empty;
        public string DueEvidencePath { get; init; } = string.Empty;
        public string OriginalClauseId { get; init; } = string.Empty;
    }

    private static Dictionary<string, VersionValue> BuildVersionValues(
        AgreementPackage p,
        bool[] active,
        HashSet<string> clauseIds,
        HashSet<string> activeNewIds,
        Dictionary<string, List<int>> targetSuccessors,
        Dictionary<string, int> newIdOwner,
        List<Issue> issues)
    {
        var values = new Dictionary<string, VersionValue>(StringComparer.Ordinal);
        var n = p.Amendments.Count;

        for (var i = 0; i < p.Clauses.Count; i++)
        {
            var c = p.Clauses[i];
            DateOnly? due = TryParseDate(c.Due, out var d) ? d : null;
            if (c.Due is not null && !due.HasValue)
            {
                issues.Add(new Issue(IssueCodes.DateInvalid, Severity.Error,
                    $"Clause '{c.Id}' due date '{c.Due}' is not a valid yyyy-MM-dd date.",
                    $"$.clauses[{i}].due"));
            }
            if (c.AmountFen is < 0)
            {
                issues.Add(new Issue(IssueCodes.AmountNegative, Severity.Error,
                    $"Clause '{c.Id}' amount is negative.",
                    $"$.clauses[{i}].amountFen"));
            }

            values[c.Id] = new VersionValue
            {
                AmountFen = c.AmountFen,
                Due = due,
                AmountEvidencePath = c.AmountFen.HasValue ? $"$.clauses[{i}].amountFen" : string.Empty,
                DueEvidencePath = due.HasValue ? $"$.clauses[{i}].due" : string.Empty,
                OriginalClauseId = c.Id
            };
        }

        var predecessor = new int?[n];
        var inDegree = new int[n];
        for (var i = 0; i < n; i++)
        {
            if (!active[i])
            {
                continue;
            }
            var target = p.Amendments[i].Replaces!;
            if (activeNewIds.Contains(target) && newIdOwner.TryGetValue(target, out var owner))
            {
                predecessor[i] = owner;
                inDegree[i] = 1;
            }
        }

        var pq = new PriorityQueue<int, int>();
        for (var i = 0; i < n; i++)
        {
            if (active[i] && inDegree[i] == 0)
            {
                pq.Enqueue(i, i);
            }
        }

        var processed = new bool[n];
        while (pq.TryDequeue(out var i, out _))
        {
            if (processed[i])
            {
                continue;
            }
            processed[i] = true;

            var a = p.Amendments[i];

            if (a.AmountFen is < 0)
            {
                issues.Add(new Issue(IssueCodes.AmountNegative, Severity.Error,
                    $"Amendment '{a.Id}' amount is negative.",
                    $"$.amendments[{i}].amountFen"));
            }
            DateOnly? amdDue = null;
            if (a.Due is not null)
            {
                if (TryParseDate(a.Due, out var dd))
                {
                    amdDue = dd;
                }
                else
                {
                    issues.Add(new Issue(IssueCodes.DateInvalid, Severity.Error,
                        $"Amendment '{a.Id}' due date '{a.Due}' is not a valid yyyy-MM-dd date.",
                        $"$.amendments[{i}].due"));
                }
            }

            var target = a.Replaces!;
            values.TryGetValue(target, out var targetValue);

            long? amount;
            string amountEv;
            if (a.AmountFen.HasValue)
            {
                amount = a.AmountFen;
                amountEv = $"$.amendments[{i}].amountFen";
            }
            else
            {
                amount = targetValue?.AmountFen;
                amountEv = targetValue?.AmountEvidencePath ?? string.Empty;
            }

            DateOnly? due;
            string dueEv;
            if (amdDue.HasValue)
            {
                due = amdDue;
                dueEv = $"$.amendments[{i}].due";
            }
            else
            {
                due = targetValue?.Due;
                dueEv = targetValue?.DueEvidencePath ?? string.Empty;
            }

            var originalClauseId = targetValue?.OriginalClauseId
                ?? (clauseIds.Contains(target) ? target : string.Empty);

            values[a.NewClauseId!] = new VersionValue
            {
                AmountFen = amount,
                Due = due,
                AmountEvidencePath = amountEv,
                DueEvidencePath = dueEv,
                OriginalClauseId = originalClauseId
            };

            if (targetSuccessors.TryGetValue(a.NewClauseId!, out var successors))
            {
                foreach (var s in successors)
                {
                    if (active[s])
                    {
                        inDegree[s]--;
                        if (inDegree[s] == 0)
                        {
                            pq.Enqueue(s, s);
                        }
                    }
                }
            }
        }

        for (var i = 0; i < n; i++)
        {
            if (active[i] && !processed[i])
            {
                issues.Add(new Issue(IssueCodes.AmendmentAmbiguous, Severity.Error,
                    $"Amendment '{p.Amendments[i].Id}' participates in a replacement chain cycle.",
                    $"$.amendments[{i}].replaces"));
            }
        }

        return values;
    }

    private static void BuildReferenceGraph(
        AgreementPackage p,
        HashSet<string> clauseIds,
        HashSet<string> activeNewIds,
        out Dictionary<string, int> nodeIndex,
        out List<string> nodeId,
        out List<int>[] adjacency,
        out List<string>[] edgeEvidence,
        List<Issue> issues)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var ids = new List<string>();
        var adj = new List<List<int>>();
        var edges = new List<List<string>>();

        int GetOrAddNode(string id)
        {
            if (index.TryGetValue(id, out var idx))
            {
                return idx;
            }
            idx = ids.Count;
            index[id] = idx;
            ids.Add(id);
            adj.Add(new List<int>());
            edges.Add(new List<string>());
            return idx;
        }

        for (var i = 0; i < p.Clauses.Count; i++)
        {
            GetOrAddNode(p.Clauses[i].Id);
        }
        for (var i = 0; i < p.Amendments.Count; i++)
        {
            if (activeNewIds.Contains(p.Amendments[i].NewClauseId!))
            {
                GetOrAddNode(p.Amendments[i].NewClauseId!);
            }
        }

        for (var i = 0; i < p.Clauses.Count; i++)
        {
            var c = p.Clauses[i];
            var src = GetOrAddNode(c.Id);
            for (var j = 0; j < c.References.Count; j++)
            {
                var target = c.References[j] ?? string.Empty;
                var evidence = $"$.clauses[{i}].references[{j}]";
                if (!clauseIds.Contains(target) && !activeNewIds.Contains(target))
                {
                    issues.Add(new Issue(IssueCodes.RefDangling, Severity.Error,
                        $"Clause '{c.Id}' references unknown clause or version '{target}'.",
                        evidence));
                    continue;
                }

                var dst = GetOrAddNode(target);
                adj[src].Add(dst);
                edges[src].Add(evidence);
            }
        }

        nodeIndex = index;
        nodeId = ids;
        adjacency = new List<int>[adj.Count];
        edgeEvidence = new List<string>[edges.Count];
        for (var k = 0; k < adj.Count; k++)
        {
            adjacency[k] = adj[k];
            edgeEvidence[k] = edges[k];
        }
    }

    private static void DetectReferenceCycles(
        int nodeCount,
        List<int>[] adjacency,
        List<string>[] edgeEvidence,
        List<string> nodeId,
        AgreementPackage p,
        List<Issue> issues)
    {
        var color = new int[nodeCount];
        var stackNode = new Stack<int>();
        var stackEdge = new Stack<int>();

        for (var start = 0; start < nodeCount; start++)
        {
            if (color[start] != 0)
            {
                continue;
            }

            stackNode.Clear();
            stackEdge.Clear();
            stackNode.Push(start);
            stackEdge.Push(0);
            color[start] = 1;

            while (stackNode.Count > 0)
            {
                var u = stackNode.Peek();
                var idx = stackEdge.Pop();
                var neighbors = adjacency[u];

                if (idx < neighbors.Count)
                {
                    stackEdge.Push(idx + 1);
                    var v = neighbors[idx];
                    if (color[v] == 0)
                    {
                        color[v] = 1;
                        stackNode.Push(v);
                        stackEdge.Push(0);
                    }
                    else if (color[v] == 1)
                    {
                        var cyclePath = BuildCyclePath(stackNode, v, nodeId);
                        issues.Add(new Issue(IssueCodes.RefCycle, Severity.Error,
                            $"Reference cycle detected: {cyclePath}.",
                            edgeEvidence[u][idx]));
                    }
                }
                else
                {
                    color[u] = 2;
                    stackNode.Pop();
                }
            }
        }
    }

    private static string BuildCyclePath(Stack<int> stackNode, int target, List<string> nodeId)
    {
        var reversed = stackNode.ToArray();
        var sb = new System.Text.StringBuilder();
        var started = false;
        for (var i = reversed.Length - 1; i >= 0; i--)
        {
            if (reversed[i] == target)
            {
                started = true;
            }
            if (started)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" -> ");
                }
                sb.Append(nodeId[reversed[i]]);
            }
        }
        sb.Append(" -> ").Append(nodeId[target]);
        return sb.ToString();
    }

    private static void CheckAmountsAndDates(
        AgreementPackage p,
        Dictionary<string, VersionValue> values,
        Dictionary<string, string> successorVersion,
        List<Issue> issues)
    {
        for (var i = 0; i < p.Clauses.Count; i++)
        {
            var c = p.Clauses[i];
            var selfFinal = ResolveFinal(c.Id, values, successorVersion);
            if (selfFinal is null)
            {
                continue;
            }
            var selfValue = selfFinal.Value.Value;

            var referencedAmounts = new List<long>();
            var allAmountsKnown = true;
            var allDatesKnown = true;
            DateOnly? minReferencedDue = null;

            foreach (var refId in c.References)
            {
                var resolved = ResolveFinal(refId, values, successorVersion);
                if (resolved is null)
                {
                    allAmountsKnown = false;
                    allDatesKnown = false;
                    continue;
                }
                var rv = resolved.Value.Value;

                if (rv.AmountFen.HasValue)
                {
                    referencedAmounts.Add(rv.AmountFen.Value);
                }
                else
                {
                    allAmountsKnown = false;
                }

                if (rv.Due.HasValue)
                {
                    if (!minReferencedDue.HasValue || rv.Due.Value < minReferencedDue.Value)
                    {
                        minReferencedDue = rv.Due;
                    }
                }
                else
                {
                    allDatesKnown = false;
                }
            }

            if (selfValue.Due.HasValue && allDatesKnown && minReferencedDue.HasValue
                && selfValue.Due.Value < minReferencedDue.Value)
            {
                issues.Add(new Issue(IssueCodes.DateBeforeReferenced, Severity.Error,
                    $"Clause '{c.Id}' is due {selfValue.Due.Value:yyyy-MM-dd}, before a referenced clause due {minReferencedDue.Value:yyyy-MM-dd}.",
                    selfValue.DueEvidencePath.Length > 0 ? selfValue.DueEvidencePath : $"$.clauses[{i}].due"));
            }

            if (selfValue.AmountFen.HasValue && c.References.Count > 0 && allAmountsKnown)
            {
                long sum = 0;
                foreach (var a in referencedAmounts)
                {
                    sum += a;
                }
                if (selfValue.AmountFen.Value != sum)
                {
                    issues.Add(new Issue(IssueCodes.AmountSumMismatch, Severity.Error,
                        $"Clause '{c.Id}' amount {selfValue.AmountFen.Value} does not equal the sum {sum} of its referenced clauses.",
                        selfValue.AmountEvidencePath.Length > 0 ? selfValue.AmountEvidencePath : $"$.clauses[{i}].amountFen"));
                }
            }
        }
    }

    private static (string VersionId, VersionValue Value)? ResolveFinal(
        string id,
        Dictionary<string, VersionValue> values,
        Dictionary<string, string> successorVersion)
    {
        if (!values.TryGetValue(id, out var current))
        {
            return null;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { id };
        var curId = id;
        while (successorVersion.TryGetValue(curId, out var nextId))
        {
            if (!visited.Add(nextId) || !values.TryGetValue(nextId, out var next))
            {
                return (curId, current);
            }
            curId = nextId;
            current = next;
        }
        return (curId, current);
    }

    private static void CheckSignatures(
        AgreementPackage p,
        HashSet<string> partyIds,
        List<string> partyOrder,
        HashSet<string> clauseIds,
        Dictionary<string, int> amendmentById,
        bool[] active,
        HashSet<string> activeNewIds,
        Dictionary<string, List<int>> targetSuccessors,
        Dictionary<string, VersionValue> values,
        List<Issue> issues)
    {
        var partySignsAgreement = new HashSet<string>(StringComparer.Ordinal);
        var partySignsAmendment = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        for (var i = 0; i < p.Signatures.Count; i++)
        {
            var s = p.Signatures[i];
            var pid = s.PartyId ?? string.Empty;
            if (!partyIds.Contains(pid))
            {
                issues.Add(new Issue(IssueCodes.SignaturePartyUnknown, Severity.Error,
                    $"Signature belongs to unknown party '{pid}'.",
                    $"$.signatures[{i}].partyId"));
            }

            for (var j = 0; j < s.Scope.Count; j++)
            {
                var sc = s.Scope[j] ?? string.Empty;
                var path = $"$.signatures[{i}].scope[{j}]";
                if (sc == p.AgreementId)
                {
                    if (pid.Length > 0)
                    {
                        partySignsAgreement.Add(pid);
                    }
                    continue;
                }

                if (!amendmentById.TryGetValue(sc, out var amdIdx))
                {
                    issues.Add(new Issue(IssueCodes.SignatureScopeUnknown, Severity.Error,
                        $"Signature scope references unknown agreement or amendment '{sc}'.",
                        path));
                    continue;
                }

                if (pid.Length > 0)
                {
                    if (!partySignsAmendment.TryGetValue(pid, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        partySignsAmendment[pid] = set;
                    }
                    set.Add(sc);
                }

                if (amdIdx < active.Length && !active[amdIdx])
                {
                    issues.Add(new Issue(IssueCodes.SignatureScopeWithdrawn, Severity.Warning,
                        $"Signature covers withdrawn amendment '{sc}'.",
                        path));
                }
            }
        }

        for (var i = 0; i < p.Parties.Count; i++)
        {
            var pid = p.Parties[i].Id;
            if (!partySignsAgreement.Contains(pid))
            {
                issues.Add(new Issue(IssueCodes.SignatureMissing, Severity.Error,
                    $"Party '{pid}' has not signed the agreement.",
                    $"$.parties[{i}].id"));
            }
        }

        for (var i = 0; i < p.Amendments.Count; i++)
        {
            if (!active[i])
            {
                continue;
            }

            var a = p.Amendments[i];
            var rootId = FindOriginalClauseId(a.Replaces!, values, clauseIds, activeNewIds);
            if (rootId is null || !values.TryGetValue(rootId, out var rootValue))
            {
                continue;
            }

            var obligor = FindObligor(rootId, p);
            if (obligor is null || !partyIds.Contains(obligor))
            {
                continue;
            }

            if (!partySignsAmendment.TryGetValue(obligor, out var signed) || !signed.Contains(a.Id))
            {
                issues.Add(new Issue(IssueCodes.SignatureAmdMissing, Severity.Error,
                    $"Active amendment '{a.Id}' affecting obligor '{obligor}' is not signed by that obligor.",
                    $"$.amendments[{i}].id"));
            }
        }
    }

    private static string? FindOriginalClauseId(
        string target,
        Dictionary<string, VersionValue> values,
        HashSet<string> clauseIds,
        HashSet<string> activeNewIds)
    {
        if (clauseIds.Contains(target))
        {
            return target;
        }
        if (values.TryGetValue(target, out var v) && v.OriginalClauseId.Length > 0)
        {
            return v.OriginalClauseId;
        }
        return null;
    }

    private static string? FindObligor(string originalClauseId, AgreementPackage p)
    {
        for (var i = 0; i < p.Clauses.Count; i++)
        {
            if (p.Clauses[i].Id == originalClauseId)
            {
                return p.Clauses[i].Obligor;
            }
        }
        return null;
    }

    private static Dictionary<string, Obligation> BuildEffectiveObligations(
        AgreementPackage p,
        Dictionary<string, string> successorVersion,
        Dictionary<string, VersionValue> values)
    {
        var result = new Dictionary<string, Obligation>(StringComparer.Ordinal);
        for (var i = 0; i < p.Clauses.Count; i++)
        {
            var c = p.Clauses[i];
            var final = ResolveFinal(c.Id, values, successorVersion);
            if (final is null)
            {
                continue;
            }
            var fv = final.Value.Value;
            result[c.Id] = new Obligation
            {
                ClauseVersionId = final.Value.VersionId,
                ObligorPartyId = c.Obligor ?? string.Empty,
                AmountFen = fv.AmountFen,
                Due = fv.Due,
                AmountEvidencePath = fv.AmountEvidencePath,
                DueEvidencePath = fv.DueEvidencePath
            };
        }
        return result;
    }

    private static bool TryParseDate(string? s, out DateOnly date)
    {
        if (s is not null
            && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
        {
            date = d;
            return true;
        }
        date = default;
        return false;
    }

    private static int CompareIssues(Issue a, Issue b)
    {
        var c = string.CompareOrdinal(a.Code, b.Code);
        if (c != 0)
        {
            return c;
        }
        c = string.CompareOrdinal(a.EvidencePath, b.EvidencePath);
        if (c != 0)
        {
            return c;
        }
        return string.CompareOrdinal(a.Message, b.Message);
    }
}
