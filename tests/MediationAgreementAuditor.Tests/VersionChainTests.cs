using System.Text.Json;
using MediationAgreementAuditor;

namespace MediationAgreementAuditor.Tests;

public class VersionChainTests
{
    private static AgreementPackage BasePackage()
    {
        return new AgreementPackage
        {
            AgreementId = "AGR-CHAIN",
            Parties = new List<Party>
            {
                new() { Id = "PTY-A", Name = "Alice" },
                new() { Id = "PTY-B", Name = "Bob" }
            },
            Clauses = new List<Clause>
            {
                new()
                {
                    Id = "CL-01", Obligor = "PTY-A", AmountFen = 10000,
                    Due = "2026-09-01", References = new List<string>()
                }
            },
            Amendments = new List<Amendment>(),
            Signatures = new List<Signature>
            {
                new() { PartyId = "PTY-A", Scope = new List<string> { "AGR-CHAIN" } },
                new() { PartyId = "PTY-B", Scope = new List<string> { "AGR-CHAIN" } }
            }
        };
    }

    private static void SignAll(AgreementPackage pkg, string partyId, IEnumerable<string> amendmentIds)
    {
        var sig = pkg.Signatures.First(s => s.PartyId == partyId);
        foreach (var id in amendmentIds)
        {
            sig.Scope.Add(id);
        }
    }

    private static string Serialize(AgreementPackage pkg) =>
        JsonSerializer.Serialize(pkg, Fixtures.WriteOptions);

    [Fact]
    public void LinearChain_ExposesAllLinks_AndResolvesLeaf()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-R1", Replaces = "CL-01", NewClauseId = "CL-01-R1",
            AmountFen = 8000, Due = "2026-10-01"
        });
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-R2", Replaces = "CL-01-R1", NewClauseId = "CL-01-R2",
            AmountFen = 6000, Due = "2026-11-01"
        });
        SignAll(pkg, "PTY-A", new[] { "AMD-R1", "AMD-R2" });

        var result = AgreementAuditor.AuditJson(Serialize(pkg));

        Assert.False(result.HasErrors, string.Join("; ", result.Issues.Select(i => i.Code)));
        var chain = Assert.Single(result.VersionChains);
        Assert.Equal("CL-01", chain.RootClauseId);
        Assert.Equal(2, chain.Links.Count);
        Assert.Equal("CL-01", chain.Links[0].FromVersionId);
        Assert.Equal("CL-01-R1", chain.Links[0].ToVersionId);
        Assert.Equal("AMD-R1", chain.Links[0].AmendmentId);
        Assert.Equal("CL-01-R1", chain.Links[1].FromVersionId);
        Assert.Equal("CL-01-R2", chain.Links[1].ToVersionId);
        Assert.Equal("AMD-R2", chain.Links[1].AmendmentId);
        Assert.Equal("CL-01-R2", chain.EffectiveVersionId);
        Assert.False(chain.HasFork);
        Assert.False(chain.HasCycle);

        var obl = result.EffectiveObligations["CL-01"];
        Assert.Equal("CL-01-R2", obl.ClauseVersionId);
        Assert.Equal(6000, obl.AmountFen);
        Assert.Equal("$.amendments[1].amountFen", obl.AmountEvidencePath);
    }

    [Fact]
    public void AmendmentOrderSwap_ProducesSameEffectiveView_AndCodes()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-R1", Replaces = "CL-01", NewClauseId = "CL-01-R1",
            AmountFen = 8000, Due = "2026-10-01"
        });
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-R2", Replaces = "CL-01-R1", NewClauseId = "CL-01-R2",
            AmountFen = 6000, Due = "2026-11-01"
        });
        SignAll(pkg, "PTY-A", new[] { "AMD-R1", "AMD-R2" });

        var resultA = AgreementAuditor.AuditJson(Serialize(pkg));

        var swapped = BasePackage();
        swapped.Amendments.Add(new Amendment
        {
            Id = "AMD-R2", Replaces = "CL-01-R1", NewClauseId = "CL-01-R2",
            AmountFen = 6000, Due = "2026-11-01"
        });
        swapped.Amendments.Add(new Amendment
        {
            Id = "AMD-R1", Replaces = "CL-01", NewClauseId = "CL-01-R1",
            AmountFen = 8000, Due = "2026-10-01"
        });
        SignAll(swapped, "PTY-A", new[] { "AMD-R1", "AMD-R2" });

        var resultB = AgreementAuditor.AuditJson(Serialize(swapped));

        Assert.Equal(resultA.HasErrors, resultB.HasErrors);
        Assert.Equal(
            resultA.Issues.Select(i => i.Code).OrderBy(c => c, StringComparer.Ordinal),
            resultB.Issues.Select(i => i.Code).OrderBy(c => c, StringComparer.Ordinal));

        Assert.Equal(resultA.Issues.Count, resultB.Issues.Count);
        for (var i = 0; i < resultA.Issues.Count; i++)
        {
            Assert.Equal(resultA.Issues[i].Code, resultB.Issues[i].Code);
        }

        var oblA = resultA.EffectiveObligations["CL-01"];
        var oblB = resultB.EffectiveObligations["CL-01"];
        Assert.Equal(oblA.ClauseVersionId, oblB.ClauseVersionId);
        Assert.Equal(oblA.AmountFen, oblB.AmountFen);
        Assert.Equal(oblA.Due, oblB.Due);

        var chainA = resultA.VersionChains[0];
        var chainB = resultB.VersionChains[0];
        Assert.Equal(chainA.EffectiveVersionId, chainB.EffectiveVersionId);
        Assert.Equal(chainA.Links.Count, chainB.Links.Count);
        Assert.Equal(
            chainA.Links.Select(l => l.AmendmentId),
            chainB.Links.Select(l => l.AmendmentId));
    }

    [Fact]
    public void Fork_ReportsSingleConflict_WithShortestStableEvidence_AndDoesNotResolve()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-B", Replaces = "CL-01", NewClauseId = "CL-01-B",
            AmountFen = 2000, Due = "2026-12-01"
        });
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-A", Replaces = "CL-01", NewClauseId = "CL-01-A",
            AmountFen = 1000, Due = "2026-11-01"
        });
        SignAll(pkg, "PTY-A", new[] { "AMD-A", "AMD-B" });

        var result = AgreementAuditor.AuditJson(Serialize(pkg));

        var ambiguous = result.Issues.Where(i => i.Code == IssueCodes.AmendmentAmbiguous).ToList();
        Assert.Single(ambiguous);
        Assert.Equal("$.amendments[1].replaces", ambiguous[0].EvidencePath);

        var chain = Assert.Single(result.VersionChains);
        Assert.True(chain.HasFork);
        Assert.False(chain.HasCycle);
        Assert.Null(chain.EffectiveVersionId);
        Assert.Equal(2, chain.CandidateVersionIds.Count);
        Assert.Contains("CL-01-A", chain.CandidateVersionIds);
        Assert.Contains("CL-01-B", chain.CandidateVersionIds);
        Assert.Equal("$.amendments[1].replaces", chain.ConflictEvidencePath);

        Assert.False(result.EffectiveObligations.ContainsKey("CL-01"));
    }

    [Fact]
    public void ForkOrderSwap_SameCodesAndConflict_ButEvidencePointsToCorrectPosition()
    {
        var pkgA = BasePackage();
        pkgA.Amendments.Add(new Amendment
        {
            Id = "AMD-A", Replaces = "CL-01", NewClauseId = "CL-01-A", AmountFen = 1
        });
        pkgA.Amendments.Add(new Amendment
        {
            Id = "AMD-B", Replaces = "CL-01", NewClauseId = "CL-01-B", AmountFen = 2
        });
        SignAll(pkgA, "PTY-A", new[] { "AMD-A", "AMD-B" });

        var pkgB = BasePackage();
        pkgB.Amendments.Add(new Amendment
        {
            Id = "AMD-B", Replaces = "CL-01", NewClauseId = "CL-01-B", AmountFen = 2
        });
        pkgB.Amendments.Add(new Amendment
        {
            Id = "AMD-A", Replaces = "CL-01", NewClauseId = "CL-01-A", AmountFen = 1
        });
        SignAll(pkgB, "PTY-A", new[] { "AMD-A", "AMD-B" });

        var rA = AgreementAuditor.AuditJson(Serialize(pkgA));
        var rB = AgreementAuditor.AuditJson(Serialize(pkgB));

        var codeA = rA.Issues.Select(i => i.Code).OrderBy(c => c).ToList();
        var codeB = rB.Issues.Select(i => i.Code).OrderBy(c => c).ToList();
        Assert.Equal(codeA, codeB);

        var chainA = rA.VersionChains[0];
        var chainB = rB.VersionChains[0];
        Assert.True(chainA.HasFork);
        Assert.True(chainB.HasFork);
        Assert.Null(chainA.EffectiveVersionId);
        Assert.Null(chainB.EffectiveVersionId);
        Assert.Equal(chainA.CandidateVersionIds.OrderBy(c => c), chainB.CandidateVersionIds.OrderBy(c => c));

        var issueA = Assert.Single(rA.Issues, i => i.Code == IssueCodes.AmendmentAmbiguous);
        var issueB = Assert.Single(rB.Issues, i => i.Code == IssueCodes.AmendmentAmbiguous);
        Assert.EndsWith(".replaces", issueA.EvidencePath);
        Assert.EndsWith(".replaces", issueB.EvidencePath);
        Assert.NotEqual(issueA.EvidencePath, issueB.EvidencePath);
    }

    [Fact]
    public void ReplacementCycle_ReportsBackEdge_AndDoesNotResolve()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-A", Replaces = "CL-01", NewClauseId = "CL-01-A",
            AmountFen = 1000, Due = "2026-11-01"
        });
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-B", Replaces = "CL-01-A", NewClauseId = "CL-01-B",
            AmountFen = 2000, Due = "2026-12-01"
        });
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-C", Replaces = "CL-01-B", NewClauseId = "CL-01-A",
            AmountFen = 3000, Due = "2027-01-01"
        });
        SignAll(pkg, "PTY-A", new[] { "AMD-A", "AMD-B", "AMD-C" });

        var result = AgreementAuditor.AuditJson(Serialize(pkg));

        var cycleIssue = result.Issues.FirstOrDefault(i => i.Code == IssueCodes.AmendmentAmbiguous);
        Assert.NotNull(cycleIssue);
        Assert.EndsWith(".replaces", cycleIssue.EvidencePath);

        var chain = Assert.Single(result.VersionChains);
        Assert.True(chain.HasCycle);
        Assert.Null(chain.EffectiveVersionId);
        Assert.NotNull(chain.ConflictEvidencePath);
    }

    [Fact]
    public void UnknownOriginalClause_ReportedAndChainNotResolved()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-X", Replaces = "CL-404", NewClauseId = "CL-404-R1",
            AmountFen = 100
        });
        SignAll(pkg, "PTY-A", new[] { "AMD-X" });

        var result = AgreementAuditor.AuditJson(Serialize(pkg));

        Assert.Contains(result.Issues, i => i.Code == IssueCodes.AmendmentTargetMissing);
        var chain = result.VersionChains[0];
        Assert.Equal("CL-01", chain.RootClauseId);
        Assert.Equal("CL-01", chain.EffectiveVersionId);
        Assert.Empty(chain.Links);
    }

    [Fact]
    public void OldVersions_AreNeverDeleted_AllRemainReachable()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-R1", Replaces = "CL-01", NewClauseId = "CL-01-R1",
            AmountFen = 8000
        });
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-R2", Replaces = "CL-01-R1", NewClauseId = "CL-01-R2",
            AmountFen = 6000
        });
        SignAll(pkg, "PTY-A", new[] { "AMD-R1", "AMD-R2" });

        var result = AgreementAuditor.AuditJson(Serialize(pkg));
        var chain = result.VersionChains[0];

        var allVersions = new HashSet<string> { chain.RootClauseId };
        foreach (var link in chain.Links)
        {
            allVersions.Add(link.ToVersionId);
        }
        Assert.Contains("CL-01", allVersions);
        Assert.Contains("CL-01-R1", allVersions);
        Assert.Contains("CL-01-R2", allVersions);
        Assert.Equal(3, allVersions.Count);
    }

    [Fact]
    public void ChainAppendix_ReplacesPreviousVersion_InheritsUnspecifiedFields()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-R1", Replaces = "CL-01", NewClauseId = "CL-01-R1",
            AmountFen = 8000, Due = "2026-10-01"
        });
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-R2", Replaces = "CL-01-R1", NewClauseId = "CL-01-R2"
        });
        SignAll(pkg, "PTY-A", new[] { "AMD-R1", "AMD-R2" });

        var result = AgreementAuditor.AuditJson(Serialize(pkg));
        Assert.False(result.HasErrors, string.Join("; ", result.Issues.Select(i => i.Code)));

        var obl = result.EffectiveObligations["CL-01"];
        Assert.Equal("CL-01-R2", obl.ClauseVersionId);
        Assert.Equal(8000, obl.AmountFen);
        Assert.Equal("$.amendments[0].amountFen", obl.AmountEvidencePath);
        Assert.NotNull(obl.Due);
    }

    [Fact]
    public void DeepReplacementChain_DoesNotStackOverflow()
    {
        var pkg = BasePackage();
        const int n = 5000;
        for (var i = 0; i < n; i++)
        {
            var target = i == 0 ? "CL-01" : $"CL-01-R{i:D5}";
            pkg.Amendments.Add(new Amendment
            {
                Id = $"AMD-{i:D5}", Replaces = target, NewClauseId = $"CL-01-R{i + 1:D5}",
                AmountFen = 10000 - i
            });
        }
        SignAll(pkg, "PTY-A", Enumerable.Range(0, n).Select(i => $"AMD-{i:D5}"));

        var result = AgreementAuditor.AuditJson(Serialize(pkg));
        Assert.False(result.HasErrors, string.Join("; ", result.Issues.Select(i => i.Code).Distinct()));
        var chain = result.VersionChains[0];
        Assert.Equal(n, chain.Links.Count);
        Assert.Equal($"CL-01-R{n:D5}", chain.EffectiveVersionId);
    }
}
