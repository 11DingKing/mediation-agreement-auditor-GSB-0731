using System.Text.Json;
using System.Text.Json.Nodes;
using MediationAgreementAuditor;

namespace MediationAgreementAuditor.Tests;

public class RobustnessTests
{
    private static bool IssuesEqual(Issue a, Issue b) =>
        a.Code == b.Code && a.Severity == b.Severity &&
        a.EvidencePath == b.EvidencePath && a.Message == b.Message;

    [Fact]
    public void DuplicateSubmission_ProducesIdenticalResults()
    {
        var pkg = BuildRandomPackage(new Random(42), clauses: 30, amendments: 10, parties: 3);
        var result = Fixtures.AuditRoundTrip(pkg, out var json, out _);
        var result2 = AgreementAuditor.AuditJson(json);

        Assert.Equal(result.Issues.Count, result2.Issues.Count);
        for (var i = 0; i < result.Issues.Count; i++)
        {
            Assert.True(IssuesEqual(result.Issues[i], result2.Issues[i]),
                $"Mismatch at {i}: {result.Issues[i].Code} vs {result2.Issues[i].Code}");
        }
    }

    [Fact]
    public void Issues_AreSortedDeterministically()
    {
        var pkg = BuildRandomPackage(new Random(7), clauses: 40, amendments: 12, parties: 3);
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        for (var i = 1; i < result.Issues.Count; i++)
        {
            var prev = result.Issues[i - 1];
            var cur = result.Issues[i];
            var cmp = string.CompareOrdinal(prev.Code, cur.Code);
            if (cmp == 0)
            {
                cmp = string.CompareOrdinal(prev.EvidencePath, cur.EvidencePath);
            }
            if (cmp == 0)
            {
                cmp = string.CompareOrdinal(prev.Message, cur.Message);
            }
            Assert.True(cmp <= 0,
                $"Issues not sorted at {i}: {prev.Code} {prev.EvidencePath} > {cur.Code} {cur.EvidencePath}");
        }
    }

    [Fact]
    public void AllIssueCodes_UseMedPrefix()
    {
        var pkg = BuildRandomPackage(new Random(99), clauses: 50, amendments: 15, parties: 4);
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        Assert.All(result.Issues, i => Assert.StartsWith("MED_", i.Code));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(123)]
    [InlineData(2026)]
    public void RandomGraphs_NoCrash_Deterministic_EvidencePathsValid(int seed)
    {
        var pkg = BuildRandomPackage(new Random(seed), clauses: 60, amendments: 20, parties: 4);
        var result = Fixtures.AuditRoundTrip(pkg, out var json, out var doc);
        var result2 = AgreementAuditor.AuditJson(json);

        Assert.Equal(result.Issues.Count, result2.Issues.Count);
        for (var i = 0; i < result.Issues.Count; i++)
        {
            Assert.True(IssuesEqual(result.Issues[i], result2.Issues[i]));
            Assert.True(Fixtures.EvidencePathExists(doc, result.Issues[i].EvidencePath),
                $"Evidence path does not resolve in JSON: {result.Issues[i].EvidencePath}");
        }
    }

    [Fact]
    public void DeepReferenceChain_DoesNotStackOverflow()
    {
        const int n = 10000;
        var pkg = Fixtures.ValidPackage();
        pkg.Clauses.Clear();
        for (var i = 0; i < n; i++)
        {
            var c = new Clause
            {
                Id = $"CL-{i:D5}", Obligor = "PTY-A", AmountFen = 100, Due = "2026-09-01"
            };
            if (i + 1 < n)
            {
                c.References.Add($"CL-{i + 1:D5}");
            }
            pkg.Clauses.Add(c);
        }

        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        Assert.False(result.HasErrors, string.Join("; ", result.Issues.Select(i => i.Code)));
    }

    [Fact]
    public void DeepReferenceCycle_DoesNotStackOverflow_AndReportsCycle()
    {
        const int n = 10000;
        var pkg = Fixtures.ValidPackage();
        pkg.Clauses.Clear();
        for (var i = 0; i < n; i++)
        {
            var next = (i + 1) % n;
            pkg.Clauses.Add(new Clause
            {
                Id = $"CL-{i:D5}", Obligor = "PTY-A", AmountFen = i, Due = "2026-09-01",
                References = new List<string> { $"CL-{next:D5}" }
            });
        }

        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        Assert.Contains(result.Issues, i => i.Code == IssueCodes.RefCycle);
    }

    [Fact]
    public void DeepReplacementChain_DoesNotStackOverflow()
    {
        const int n = 5000;
        var pkg = Fixtures.ValidPackage();
        for (var i = 0; i < n; i++)
        {
            var target = i == 0 ? "CL-01" : $"CL-01-R{i:D5}";
            pkg.Amendments.Add(new Amendment
            {
                Id = $"AMD-{i:D5}",
                Replaces = target,
                NewClauseId = $"CL-01-R{i + 1:D5}",
                AmountFen = 10000 - i
            });
        }
        foreach (var s in pkg.Signatures)
        {
            if (s.PartyId == "PTY-A")
            {
                for (var i = 0; i < n; i++)
                {
                    s.Scope.Add($"AMD-{i:D5}");
                }
            }
        }

        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        Assert.False(result.HasErrors, string.Join("; ", result.Issues.Select(i => i.Code).Distinct()));
        Assert.True(result.EffectiveObligations.TryGetValue("CL-01", out var obl));
        Assert.Equal(10000 - (n - 1), obl!.AmountFen);
        Assert.Equal($"CL-01-R{n:D5}", obl.ClauseVersionId);
    }

    [Fact]
    public void DeepWithdrawalCycle_DoesNotStackOverflow()
    {
        const int n = 5000;
        var pkg = Fixtures.ValidPackage();
        for (var i = 0; i < n; i++)
        {
            var next = (i + 1) % n;
            pkg.Amendments.Add(new Amendment
            {
                Id = $"AMD-{i:D5}",
                Replaces = "CL-01",
                NewClauseId = $"CL-X-{i:D5}",
                Withdraws = $"AMD-{next:D5}"
            });
        }

        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        Assert.Contains(result.Issues, i => i.Code == IssueCodes.AmendmentWithdrawCycle);
    }

    [Fact]
    public void FieldOrder_DoesNotAffectResults()
    {
        var orderA = """
        {
          "agreementId": "AGR-ORDER",
          "parties": [{"id": "PTY-A", "name": "Alice"}, {"id": "PTY-B", "name": "Bob"}],
          "clauses": [
            {"id": "CL-01", "obligor": "PTY-A", "amountFen": 10000, "due": "2026-09-01", "references": ["CL-02"]},
            {"id": "CL-02", "obligor": "PTY-B", "amountFen": 5000, "due": "2026-08-01", "references": []}
          ],
          "amendments": [
            {"id": "AMD-01", "replaces": "CL-01", "newClauseId": "CL-01-R1", "amountFen": 9000, "due": "2026-10-01"}
          ],
          "signatures": [
            {"partyId": "PTY-A", "scope": ["AGR-ORDER", "AMD-01"], "signedAt": "2026-08-01T10:00:00Z"},
            {"partyId": "PTY-B", "scope": ["AGR-ORDER"], "signedAt": "2026-08-01T10:02:00Z"}
          ]
        }
        """;

        var orderB = """
        {
          "signatures": [
            {"signedAt": "2026-08-01T10:02:00Z", "scope": ["AGR-ORDER"], "partyId": "PTY-B"},
            {"signedAt": "2026-08-01T10:00:00Z", "scope": ["AMD-01", "AGR-ORDER"], "partyId": "PTY-A"}
          ],
          "amendments": [
            {"due": "2026-10-01", "amountFen": 9000, "newClauseId": "CL-01-R1", "replaces": "CL-01", "id": "AMD-01"}
          ],
          "clauses": [
            {"references": [], "due": "2026-08-01", "amountFen": 5000, "obligor": "PTY-B", "id": "CL-02"},
            {"references": ["CL-02"], "due": "2026-09-01", "amountFen": 10000, "obligor": "PTY-A", "id": "CL-01"}
          ],
          "parties": [{"name": "Bob", "id": "PTY-B"}, {"name": "Alice", "id": "PTY-A"}],
          "agreementId": "AGR-ORDER"
        }
        """;

        var a = AgreementAuditor.AuditJson(orderA);
        var b = AgreementAuditor.AuditJson(orderB);

        Assert.Equal(a.Issues.Count, b.Issues.Count);
        for (var i = 0; i < a.Issues.Count; i++)
        {
            Assert.True(IssuesEqual(a.Issues[i], b.Issues[i]),
                $"Diff at {i}: {a.Issues[i].Code}/{a.Issues[i].EvidencePath} vs {b.Issues[i].Code}/{b.Issues[i].EvidencePath}");
        }
    }

    [Fact]
    public void EvidencePaths_ResolveInSampleJson()
    {
        var json = File.ReadAllText("materials/agreements.json");
        var doc = JsonDocument.Parse(json);
        var result = AgreementAuditor.AuditJson(json);
        Assert.NotEmpty(result.Issues);
        Assert.All(result.Issues, i =>
            Assert.True(Fixtures.EvidencePathExists(doc, i.EvidencePath),
                $"Bad path: {i.EvidencePath}"));
    }

    private static AgreementPackage BuildRandomPackage(Random rnd, int clauses, int amendments, int parties)
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Parties.Clear();
        for (var i = 0; i < parties; i++)
        {
            pkg.Parties.Add(new Party { Id = $"PTY-{i}", Name = $"Party{i}" });
        }

        pkg.Clauses.Clear();
        for (var i = 0; i < clauses; i++)
        {
            var c = new Clause
            {
                Id = $"CL-{i:D4}",
                Obligor = $"PTY-{rnd.Next(parties)}",
                AmountFen = rnd.Next(0, 100000),
                Due = $"2026-{rnd.Next(1, 13):D2}-{rnd.Next(1, 29):D2}"
            };
            var refCount = rnd.Next(0, 4);
            for (var k = 0; k < refCount; k++)
            {
                if (rnd.Next(5) == 0)
                {
                    c.References.Add($"CL-GHOST-{rnd.Next(1000)}");
                }
                else
                {
                    c.References.Add($"CL-{rnd.Next(clauses):D4}");
                }
            }
            pkg.Clauses.Add(c);
        }

        pkg.Amendments.Clear();
        for (var i = 0; i < amendments; i++)
        {
            var amd = new Amendment { Id = $"AMD-{i:D3}" };
            var roll = rnd.Next(10);
            if (roll < 7)
            {
                amd.Replaces = rnd.Next(3) == 0
                    ? $"CL-GHOST-{rnd.Next(1000)}"
                    : $"CL-{rnd.Next(clauses):D4}";
                amd.NewClauseId = $"CL-VER-{i:D3}";
                if (rnd.Next(2) == 0)
                {
                    amd.AmountFen = rnd.Next(0, 100000);
                }
                if (rnd.Next(2) == 0)
                {
                    amd.Due = $"2026-{rnd.Next(1, 13):D2}-{rnd.Next(1, 29):D2}";
                }
            }

            if (i > 0 && rnd.Next(3) == 0)
            {
                amd.Withdraws = $"AMD-{rnd.Next(i):D3}";
            }

            if (amd.Replaces is null && amd.Withdraws is null)
            {
                amd.Replaces = $"CL-{rnd.Next(clauses):D4}";
                amd.NewClauseId = $"CL-VER-{i:D3}";
            }
            if (amd.Replaces is not null && amd.NewClauseId is null)
            {
                amd.NewClauseId = $"CL-VER-{i:D3}";
            }

            pkg.Amendments.Add(amd);
        }

        pkg.Signatures.Clear();
        foreach (var party in pkg.Parties)
        {
            var scope = new List<string> { pkg.AgreementId };
            foreach (var amd in pkg.Amendments)
            {
                if (rnd.Next(2) == 0)
                {
                    scope.Add(amd.Id);
                }
            }
            pkg.Signatures.Add(new Signature
            {
                PartyId = party.Id,
                Scope = scope,
                SignedAt = "2026-08-01T10:00:00Z"
            });
        }

        return pkg;
    }
}
