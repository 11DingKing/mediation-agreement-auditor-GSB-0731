using System.Text.Json;
using MediationAgreementAuditor;

namespace MediationAgreementAuditor.Tests;

public class AuditorTests
{
    private static void AssertContains(IReadOnlyList<Issue> issues, string code, string? evidencePrefix = null)
    {
        var match = issues.FirstOrDefault(i => i.Code == code);
        Assert.True(match is not null,
            $"Expected issue code {code} but found: {string.Join(", ", issues.Select(i => i.Code))}");
        if (evidencePrefix is not null)
        {
            Assert.StartsWith(evidencePrefix, match!.EvidencePath);
        }
    }

    private static void AssertNotContains(IReadOnlyList<Issue> issues, string code)
    {
        Assert.DoesNotContain(issues, i => i.Code == code);
    }

    [Fact]
    public void SampleFixture_FindsExpectedIssues()
    {
        var json = File.ReadAllText("materials/agreements.json");
        var result = AgreementAuditor.AuditJson(json);

        Assert.Equal("AGR-2026-018", result.AgreementId);
        AssertContains(result.Issues, IssueCodes.AmendmentTargetMissing, "$.amendments[1].replaces");
        AssertContains(result.Issues, IssueCodes.DateBeforeReferenced, "$.clauses[1].due");
        Assert.All(result.Issues, i => Assert.StartsWith("MED_", i.Code));
    }

    [Fact]
    public void ValidPackage_HasNoIssues()
    {
        var result = Fixtures.AuditRoundTrip(Fixtures.ValidPackage(), out _, out _);
        Assert.Empty(result.Issues);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void PartyDuplicate_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Parties.Add(new Party { Id = "PTY-A", Name = "Alice Duplicate" });
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.PartyDuplicate, "$.parties[2].id");
    }

    [Fact]
    public void PartyObligorUnknown_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Clauses[0].Obligor = "PTY-Z";
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.PartyObligorUnknown, "$.clauses[0].obligor");
    }

    [Fact]
    public void ClauseDuplicate_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Clauses.Add(new Clause
        {
            Id = "CL-01", Obligor = "PTY-B", AmountFen = 500, Due = "2026-10-01"
        });
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.ClauseDuplicate, "$.clauses[1].id");
    }

    [Fact]
    public void RefDangling_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Clauses[0].References.Add("CL-404");
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.RefDangling, "$.clauses[0].references[0]");
    }

    [Fact]
    public void RefCycle_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Clauses.Add(new Clause
        {
            Id = "CL-02", Obligor = "PTY-B", AmountFen = 100, Due = "2026-10-01",
            References = new List<string> { "CL-01" }
        });
        pkg.Clauses[0].References.Add("CL-02");
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.RefCycle, "$.clauses[");
    }

    [Fact]
    public void AmendmentDuplicate_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-01", Replaces = "CL-01", NewClauseId = "CL-01-R1", AmountFen = 9000
        });
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-01", Replaces = "CL-01", NewClauseId = "CL-01-R2", AmountFen = 8000
        });
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.AmendmentDuplicate, "$.amendments[1].id");
    }

    [Fact]
    public void AmendmentMalformed_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-BAD", Replaces = "CL-01"
        });
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.AmendmentMalformed, "$.amendments[0]");
    }

    [Fact]
    public void AmendmentTargetMissing_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-X", Replaces = "CL-404", NewClauseId = "CL-404-R1"
        });
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.AmendmentTargetMissing, "$.amendments[0].replaces");
    }

    [Fact]
    public void AmendmentNewIdCollision_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-X", Replaces = "CL-01", NewClauseId = "CL-01"
        });
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.AmendmentNewIdCollision, "$.amendments[0].newClauseId");
    }

    [Fact]
    public void AmendmentAmbiguous_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-A", Replaces = "CL-01", NewClauseId = "CL-01-A", AmountFen = 1
        });
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-B", Replaces = "CL-01", NewClauseId = "CL-01-B", AmountFen = 2
        });
        AddSignatureForAmendments(pkg, "PTY-A", new[] { "AMD-A", "AMD-B" });
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.AmendmentAmbiguous, "$.amendments[");
    }

    [Fact]
    public void AmendmentWithdrawUnknown_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-W", Withdraws = "AMD-NOPE"
        });
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.AmendmentWithdrawUnknown, "$.amendments[0].withdraws");
    }

    [Fact]
    public void AmendmentWithdrawCycle_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-A", Replaces = "CL-01", NewClauseId = "CL-01-A", Withdraws = "AMD-B"
        });
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-B", Replaces = "CL-01", NewClauseId = "CL-01-B", Withdraws = "AMD-A"
        });
        AddSignatureForAmendments(pkg, "PTY-A", new[] { "AMD-A", "AMD-B" });
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.AmendmentWithdrawCycle, "$.amendments[");
    }

    [Fact]
    public void SignaturePartyUnknown_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Signatures.Add(new Signature
        {
            PartyId = "PTY-Z", Scope = new List<string> { "AGR-TEST" }
        });
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.SignaturePartyUnknown, "$.signatures[2].partyId");
    }

    [Fact]
    public void SignatureScopeUnknown_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Signatures[0].Scope.Add("AMD-NOPE");
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.SignatureScopeUnknown, "$.signatures[0].scope[1]");
    }

    [Fact]
    public void SignatureScopeWithdrawn_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-01", Replaces = "CL-01", NewClauseId = "CL-01-R1", AmountFen = 9000
        });
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-W", Withdraws = "AMD-01"
        });
        pkg.Signatures[0].Scope.Add("AMD-01");
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.SignatureScopeWithdrawn, "$.signatures[0].scope[1]");
    }

    [Fact]
    public void SignatureMissing_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Signatures.RemoveAll(s => s.PartyId == "PTY-B");
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.SignatureMissing, "$.parties[1].id");
    }

    [Fact]
    public void SignatureAmdMissing_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-01", Replaces = "CL-01", NewClauseId = "CL-01-R1", AmountFen = 9000,
            Due = "2026-09-15"
        });
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.SignatureAmdMissing, "$.amendments[0].id");
    }

    [Fact]
    public void AmountNegative_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Clauses[0].AmountFen = -1;
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.AmountNegative, "$.clauses[0].amountFen");
    }

    [Fact]
    public void AmountSumMismatch_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Clauses[0].AmountFen = 5000;
        pkg.Clauses.Add(new Clause
        {
            Id = "CL-02", Obligor = "PTY-B", AmountFen = 9999, Due = "2026-12-01",
            References = new List<string> { "CL-01" }
        });
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.AmountSumMismatch, "$.clauses[1].amountFen");
    }

    [Fact]
    public void DateInvalid_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Clauses[0].Due = "not-a-date";
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.DateInvalid, "$.clauses[0].due");
    }

    [Fact]
    public void DateBeforeReferenced_IsDetected()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Clauses[0].Due = "2026-12-01";
        pkg.Clauses.Add(new Clause
        {
            Id = "CL-02", Obligor = "PTY-B", AmountFen = 100, Due = "2026-01-01",
            References = new List<string> { "CL-01" }
        });
        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertContains(result.Issues, IssueCodes.DateBeforeReferenced, "$.clauses[1].due");
    }

    [Fact]
    public void MultipleClauseReplacements_ResolveChain()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-R1", Replaces = "CL-01", NewClauseId = "CL-01-R1", AmountFen = 8000
        });
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-R2", Replaces = "CL-01-R1", NewClauseId = "CL-01-R2", AmountFen = 6000,
            Due = "2026-11-01"
        });
        AddSignatureForAmendments(pkg, "PTY-A", new[] { "AMD-R1", "AMD-R2" });

        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        Assert.False(result.HasErrors, string.Join("; ", result.Issues.Select(i => i.Code)));
        Assert.True(result.EffectiveObligations.TryGetValue("CL-01", out var obl));
        Assert.Equal(6000, obl!.AmountFen);
        Assert.Equal("CL-01-R2", obl.ClauseVersionId);
        Assert.Equal("$.amendments[1].amountFen", obl.AmountEvidencePath);
        Assert.Equal("$.amendments[1].due", obl.DueEvidencePath);
    }

    [Fact]
    public void AmendmentWithdrawal_RestoresPriorValues()
    {
        var pkg = Fixtures.ValidPackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-R1", Replaces = "CL-01", NewClauseId = "CL-01-R1", AmountFen = 1, Due = "2026-12-01"
        });
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-W", Withdraws = "AMD-R1"
        });
        AddSignatureForAmendments(pkg, "PTY-A", new[] { "AMD-R1" });

        var result = Fixtures.AuditRoundTrip(pkg, out _, out _);
        AssertNotContains(result.Issues, IssueCodes.SignatureAmdMissing);
        Assert.True(result.EffectiveObligations.TryGetValue("CL-01", out var obl));
        Assert.Equal(10000, obl!.AmountFen);
        Assert.Contains(result.Issues, i => i.Code == IssueCodes.SignatureScopeWithdrawn);
    }

    private static void AddSignatureForAmendments(AgreementPackage pkg, string partyId, string[] amendmentIds)
    {
        var sig = pkg.Signatures.First(s => s.PartyId == partyId);
        foreach (var id in amendmentIds)
        {
            sig.Scope.Add(id);
        }
    }
}
