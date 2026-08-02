using System;
using System.Collections.Generic;

namespace MediationAudit.Tests;

/// <summary>
/// Named JSON fixtures, one per stable issue code plus a clean baseline. Each fixture is the
/// minimal package that triggers exactly the finding named in its property, so the mapping from
/// issue code to fixture is unambiguous.
/// </summary>
public static class Fixtures
{
    /// <summary>A fully consistent package that yields no findings.</summary>
    public const string Clean = """
    {
      "agreementId": "AGR-CLEAN",
      "parties": [{"id": "PTY-A", "name": "A"}, {"id": "PTY-B", "name": "B"}],
      "clauses": [
        {"id": "CL-01", "obligor": "PTY-A", "amountFen": 100, "due": "2026-08-01"},
        {"id": "CL-02", "obligor": "PTY-B", "references": ["CL-01"], "due": "2026-09-01"}
      ],
      "amendments": [],
      "signatures": [
        {"partyId": "PTY-A", "scope": ["AGR-CLEAN"], "signedAt": "2026-08-01T10:00:00Z"},
        {"partyId": "PTY-B", "scope": ["AGR-CLEAN"], "signedAt": "2026-08-01T10:01:00Z"}
      ]
    }
    """;

    /// <summary>A clause references a clause id that does not exist. Triggers MED_UNKNOWN_REFERENCE.</summary>
    public const string UnknownReference = """
    {
      "agreementId": "AGR-UR",
      "parties": [{"id": "PTY-A", "name": "A"}],
      "clauses": [
        {"id": "CL-01", "obligor": "PTY-A", "references": ["CL-404"], "due": "2026-08-01"}
      ],
      "amendments": [],
      "signatures": [{"partyId": "PTY-A", "scope": ["AGR-UR"], "signedAt": "2026-08-01T10:00:00Z"}]
    }
    """;

    /// <summary>An amendment replaces a non-existent clause. Triggers MED_AMENDMENT_TARGET_MISSING.</summary>
    public const string AmendmentTargetMissing = """
    {
      "agreementId": "AGR-ATM",
      "parties": [{"id": "PTY-A", "name": "A"}],
      "clauses": [{"id": "CL-01", "obligor": "PTY-A", "due": "2026-08-01"}],
      "amendments": [{"id": "AMD-BAD", "replaces": "CL-404", "newClauseId": "CL-X"}],
      "signatures": [{"partyId": "PTY-A", "scope": ["AGR-ATM"], "signedAt": "2026-08-01T10:00:00Z"}]
    }
    """;

    /// <summary>Two live amendments replace the same clause. Triggers MED_DUPLICATE_REPLACEMENT.</summary>
    public const string DuplicateReplacement = """
    {
      "agreementId": "AGR-DUP",
      "parties": [{"id": "PTY-A", "name": "A"}],
      "clauses": [{"id": "CL-01", "obligor": "PTY-A", "amountFen": 100, "due": "2026-08-01"}],
      "amendments": [
        {"id": "AMD-01", "replaces": "CL-01", "newClauseId": "CL-01-R1", "amountFen": 100},
        {"id": "AMD-02", "replaces": "CL-01", "newClauseId": "CL-01-R2", "amountFen": 100}
      ],
      "signatures": [{"partyId": "PTY-A", "scope": ["AGR-DUP", "AMD-01", "AMD-02"], "signedAt": "2026-08-01T10:00:00Z"}]
    }
    """;

    /// <summary>Competing live amendments declare different amounts. Triggers MED_AMOUNT_INCONSISTENT (and MED_DUPLICATE_REPLACEMENT).</summary>
    public const string AmountInconsistent = """
    {
      "agreementId": "AGR-AMT",
      "parties": [{"id": "PTY-A", "name": "A"}],
      "clauses": [{"id": "CL-01", "obligor": "PTY-A", "amountFen": 120000, "due": "2026-08-01"}],
      "amendments": [
        {"id": "AMD-01", "replaces": "CL-01", "newClauseId": "CL-01-R1", "amountFen": 100000},
        {"id": "AMD-02", "replaces": "CL-01", "newClauseId": "CL-01-R2", "amountFen": 90000}
      ],
      "signatures": [{"partyId": "PTY-A", "scope": ["AGR-AMT", "AMD-01", "AMD-02"], "signedAt": "2026-08-01T10:00:00Z"}]
    }
    """;

    /// <summary>A party has not signed a live amendment in scope. Triggers MED_SIGNATURE_SCOPE_GAP.</summary>
    public const string SignatureScopeGap = """
    {
      "agreementId": "AGR-SIG",
      "parties": [{"id": "PTY-A", "name": "A"}, {"id": "PTY-B", "name": "B"}],
      "clauses": [{"id": "CL-01", "obligor": "PTY-A", "amountFen": 100, "due": "2026-08-01"}],
      "amendments": [{"id": "AMD-01", "replaces": "CL-01", "newClauseId": "CL-01-R1", "amountFen": 90}],
      "signatures": [
        {"partyId": "PTY-A", "scope": ["AGR-SIG", "AMD-01"], "signedAt": "2026-08-01T10:00:00Z"},
        {"partyId": "PTY-B", "scope": ["AGR-SIG"], "signedAt": "2026-08-01T10:01:00Z"}
      ]
    }
    """;

    /// <summary>A prerequisite is due after its dependent clause. Triggers MED_DATE_ORDER_VIOLATION.</summary>
    public const string DateOrderViolation = """
    {
      "agreementId": "AGR-DATE",
      "parties": [{"id": "PTY-A", "name": "A"}],
      "clauses": [
        {"id": "CL-01", "obligor": "PTY-A", "due": "2026-09-01"},
        {"id": "CL-02", "obligor": "PTY-A", "references": ["CL-01"], "due": "2026-08-15"}
      ],
      "amendments": [],
      "signatures": [{"partyId": "PTY-A", "scope": ["AGR-DATE"], "signedAt": "2026-08-01T10:00:00Z"}]
    }
    """;

    /// <summary>The reference graph has a two-node cycle. Triggers MED_REFERENCE_CYCLE.</summary>
    public const string ReferenceCycle = """
    {
      "agreementId": "AGR-CYC",
      "parties": [{"id": "PTY-A", "name": "A"}],
      "clauses": [
        {"id": "CL-01", "obligor": "PTY-A", "references": ["CL-02"], "due": "2026-08-01"},
        {"id": "CL-02", "obligor": "PTY-A", "references": ["CL-01"], "due": "2026-08-01"}
      ],
      "amendments": [],
      "signatures": [{"partyId": "PTY-A", "scope": ["AGR-CYC"], "signedAt": "2026-08-01T10:00:00Z"}]
    }
    """;

    /// <summary>A signature still covers a withdrawn amendment. Triggers MED_WITHDRAWN_AMENDMENT_REFERENCED.</summary>
    public const string WithdrawnAmendmentReferenced = """
    {
      "agreementId": "AGR-WD",
      "parties": [{"id": "PTY-A", "name": "A"}],
      "clauses": [{"id": "CL-01", "obligor": "PTY-A", "amountFen": 100, "due": "2026-08-01"}],
      "amendments": [{"id": "AMD-01", "replaces": "CL-01", "newClauseId": "CL-01-R1", "amountFen": 90, "withdrawn": true}],
      "signatures": [{"partyId": "PTY-A", "scope": ["AGR-WD", "AMD-01"], "signedAt": "2026-08-01T10:00:00Z"}]
    }
    """;

    /// <summary>
    /// A multi-step revision chain CL-01 -&gt; CL-01-R1 -&gt; CL-01-R2 that is fully consistent. Used to
    /// verify the effective view (CL-01-R2) and that reordering the amendments array changes nothing.
    /// </summary>
    public const string RevisionChain = """
    {
      "agreementId": "AGR-CHAIN",
      "parties": [{"id": "PTY-A", "name": "A"}],
      "clauses": [{"id": "CL-01", "obligor": "PTY-A", "amountFen": 100, "due": "2026-08-01"}],
      "amendments": [
        {"id": "AMD-01", "replaces": "CL-01", "newClauseId": "CL-01-R1", "amountFen": 110},
        {"id": "AMD-02", "replaces": "CL-01-R1", "newClauseId": "CL-01-R2", "amountFen": 120}
      ],
      "signatures": [{"partyId": "PTY-A", "scope": ["AGR-CHAIN", "AMD-01", "AMD-02"], "signedAt": "2026-08-01T10:00:00Z"}]
    }
    """;

    /// <summary>An append amendment introduces a brand-new clause without replacing anything.</summary>
    public const string AppendAmendment = """
    {
      "agreementId": "AGR-APP",
      "parties": [{"id": "PTY-A", "name": "A"}],
      "clauses": [{"id": "CL-01", "obligor": "PTY-A", "amountFen": 100, "due": "2026-08-01"}],
      "amendments": [
        {"id": "AMD-01", "newClauseId": "CL-02", "amountFen": 200, "due": "2026-09-01"}
      ],
      "signatures": [{"partyId": "PTY-A", "scope": ["AGR-APP", "AMD-01"], "signedAt": "2026-08-01T10:00:00Z"}]
    }
    """;

    /// <summary>
    /// A forked chain: two live amendments concurrently replace CL-01 with divergent versions. Both
    /// forks are retained, the smallest-id amendment wins the effective view, and the fork surfaces
    /// as MED_DUPLICATE_REPLACEMENT / MED_AMOUNT_INCONSISTENT.
    /// </summary>
    public const string ForkedChain = """
    {
      "agreementId": "AGR-FORK",
      "parties": [{"id": "PTY-A", "name": "A"}],
      "clauses": [{"id": "CL-01", "obligor": "PTY-A", "amountFen": 100, "due": "2026-08-01"}],
      "amendments": [
        {"id": "AMD-01", "replaces": "CL-01", "newClauseId": "CL-01-A", "amountFen": 150},
        {"id": "AMD-02", "replaces": "CL-01", "newClauseId": "CL-01-B", "amountFen": 250}
      ],
      "signatures": [{"partyId": "PTY-A", "scope": ["AGR-FORK", "AMD-01", "AMD-02"], "signedAt": "2026-08-01T10:00:00Z"}]
    }
    """;

    /// <summary>
    /// A replacement cycle: CL-01 is replaced into CL-02 while CL-02 is replaced back into CL-01.
    /// Triggers MED_REPLACEMENT_CYCLE. Both clauses pre-exist so nothing is a dangling target.
    /// </summary>
    public const string ReplacementCycle = """
    {
      "agreementId": "AGR-RCYC",
      "parties": [{"id": "PTY-A", "name": "A"}],
      "clauses": [
        {"id": "CL-01", "obligor": "PTY-A", "amountFen": 100, "due": "2026-08-01"},
        {"id": "CL-02", "obligor": "PTY-A", "amountFen": 100, "due": "2026-08-01"}
      ],
      "amendments": [
        {"id": "AMD-01", "replaces": "CL-01", "newClauseId": "CL-02"},
        {"id": "AMD-02", "replaces": "CL-02", "newClauseId": "CL-01"}
      ],
      "signatures": [{"partyId": "PTY-A", "scope": ["AGR-RCYC", "AMD-01", "AMD-02"], "signedAt": "2026-08-01T10:00:00Z"}]
    }
    """;
}
