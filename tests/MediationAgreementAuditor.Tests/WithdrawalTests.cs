using System.Text.Json;
using MediationAgreementAuditor;

namespace MediationAgreementAuditor.Tests;

public class WithdrawalTests
{
    private static AgreementPackage BasePackage()
    {
        return new AgreementPackage
        {
            AgreementId = "AGR-WD",
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
            Amendments = new List<Amendment>
            {
                new()
                {
                    Id = "AMD-01", Replaces = "CL-01", NewClauseId = "CL-01-R1",
                    AmountFen = 8000, Due = "2026-10-01"
                }
            },
            Signatures = new List<Signature>
            {
                new() { Id = "SIG-A", PartyId = "PTY-A", Scope = new List<string> { "AGR-WD", "AMD-01" }, SignedAt = "2026-08-01T10:00:00Z", Seq = 1 },
                new() { Id = "SIG-B", PartyId = "PTY-B", Scope = new List<string> { "AGR-WD" }, SignedAt = "2026-08-01T10:02:00Z", Seq = 2 }
            }
        };
    }

    private static string Serialize(AgreementPackage pkg) =>
        JsonSerializer.Serialize(pkg, Fixtures.WriteOptions);

    [Fact]
    public void WithdrawAmendment_RestoresPriorClause()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-W", Withdraws = "AMD-01"
        });

        var result = AgreementAuditor.AuditJson(Serialize(pkg));

        Assert.DoesNotContain(result.Issues, i => i.Code == IssueCodes.SignatureAmdMissing);
        Assert.Contains(result.Issues, i => i.Code == IssueCodes.SignatureScopeWithdrawn);
        var obl = result.EffectiveObligations["CL-01"];
        Assert.Equal("CL-01", obl.ClauseVersionId);
        Assert.Equal(10000, obl.AmountFen);
        Assert.False(result.AgreementWithdrawn);
    }

    [Fact]
    public void WithdrawEntireAgreement_ReportsCode_AndNoEffectiveObligations()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-KILL", Withdraws = "AGR-WD"
        });

        var result = AgreementAuditor.AuditJson(Serialize(pkg));

        Assert.True(result.AgreementWithdrawn);
        Assert.Contains(result.Issues, i => i.Code == IssueCodes.AgreementWithdrawn);
        Assert.Empty(result.EffectiveObligations);
    }

    [Fact]
    public void WithdrawEntireAgreement_StableEvidencePath()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-KILL", Withdraws = "AGR-WD"
        });

        var result = AgreementAuditor.AuditJson(Serialize(pkg));
        var issue = Assert.Single(result.Issues, i => i.Code == IssueCodes.AgreementWithdrawn);
        Assert.Equal("$.amendments[1].withdraws", issue.EvidencePath);
    }

    [Fact]
    public void RemoveParty_ReportsWarning_VoidsSignatures_NoMissingSignature()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-RM", Withdraws = "PTY-B"
        });

        var result = AgreementAuditor.AuditJson(Serialize(pkg));

        Assert.Contains(result.Issues, i => i.Code == IssueCodes.PartyRemoved);
        Assert.True(result.RemovedParties.Contains("PTY-B"));
        Assert.DoesNotContain(result.Issues, i => i.Code == IssueCodes.SignatureMissing);
        Assert.DoesNotContain(result.Issues, i => i.Code == IssueCodes.SignaturePartyUnknown);
    }

    [Fact]
    public void RemoveParty_SkipsObligationsOfRemovedObligor()
    {
        var pkg = BasePackage();
        pkg.Clauses[0].Obligor = "PTY-B";
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-RM", Withdraws = "PTY-B"
        });
        pkg.Signatures[0].Scope = new List<string> { "AGR-WD" };

        var result = AgreementAuditor.AuditJson(Serialize(pkg));

        Assert.False(result.EffectiveObligations.ContainsKey("CL-01"));
    }

    [Fact]
    public void WithdrawSignatureFact_ReportsError_PreservesOriginalSignature()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-REVOKE", Withdraws = "SIG-A"
        });

        var result = AgreementAuditor.AuditJson(Serialize(pkg));

        Assert.Contains(result.Issues, i => i.Code == IssueCodes.SignatureFactWithdraw);
        var factIssue = Assert.Single(result.Issues, i => i.Code == IssueCodes.SignatureFactWithdraw);
        Assert.Equal("$.amendments[1].withdraws", factIssue.EvidencePath);

        Assert.DoesNotContain(result.Issues, i => i.Code == IssueCodes.SignatureMissing);
        Assert.DoesNotContain(result.Issues, i => i.Code == IssueCodes.SignatureAmdMissing);

        var obl = result.EffectiveObligations["CL-01"];
        Assert.Equal("CL-01-R1", obl.ClauseVersionId);
        Assert.Equal(8000, obl.AmountFen);
    }

    [Fact]
    public void WithdrawSignatureFact_DoesNotRemoveSignatureFromScope()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-REVOKE", Withdraws = "SIG-A"
        });

        var result = AgreementAuditor.AuditJson(Serialize(pkg));

        Assert.DoesNotContain(result.Issues, i => i.Code == IssueCodes.SignatureMissing);
        Assert.DoesNotContain(result.Issues, i => i.Code == IssueCodes.SignatureAmdMissing);
        Assert.Contains(result.Issues, i => i.Code == IssueCodes.SignatureFactWithdraw);

        var obl = result.EffectiveObligations["CL-01"];
        Assert.Equal("CL-01-R1", obl.ClauseVersionId);
    }

    [Fact]
    public void SameTimestamp_OrderedBySeq_NotArrayPosition()
    {
        var pkg = new AgreementPackage
        {
            AgreementId = "AGR-SEQ",
            Parties = new List<Party>
            {
                new() { Id = "PTY-A", Name = "Alice" },
                new() { Id = "PTY-B", Name = "Bob" }
            },
            Clauses = new List<Clause>
            {
                new() { Id = "CL-01", Obligor = "PTY-A", AmountFen = 1000, Due = "2026-09-01" }
            },
            Amendments = new List<Amendment>(),
            Signatures = new List<Signature>
            {
                new() { Id = "S1", PartyId = "PTY-A", Scope = new List<string>(), SignedAt = "2026-08-01T10:00:00Z", Seq = 2 },
                new() { Id = "S2", PartyId = "PTY-B", Scope = new List<string> { "AGR-SEQ" }, SignedAt = "2026-08-01T10:00:00Z", Seq = 1 },
                new() { Id = "S3", PartyId = "PTY-A", Scope = new List<string> { "AGR-SEQ" }, SignedAt = "2026-08-01T10:00:00Z", Seq = 3 }
            }
        };

        var result = AgreementAuditor.AuditJson(Serialize(pkg));
        Assert.DoesNotContain(result.Issues, i => i.Code == IssueCodes.SignatureMissing);
    }

    [Fact]
    public void SameTimestamp_NoSeq_FallsBackToArrayIndex()
    {
        var pkg = new AgreementPackage
        {
            AgreementId = "AGR-IDX",
            Parties = new List<Party>
            {
                new() { Id = "PTY-A", Name = "Alice" }
            },
            Clauses = new List<Clause>
            {
                new() { Id = "CL-01", Obligor = "PTY-A", AmountFen = 1000, Due = "2026-09-01" }
            },
            Amendments = new List<Amendment>(),
            Signatures = new List<Signature>
            {
                new() { Id = "S1", PartyId = "PTY-A", Scope = new List<string> { "AGR-IDX" }, SignedAt = "2026-08-01T10:00:00Z" }
            }
        };

        var result = AgreementAuditor.AuditJson(Serialize(pkg));
        Assert.DoesNotContain(result.Issues, i => i.Code == IssueCodes.SignatureMissing);
    }

    [Fact]
    public void OutOfOrderArrival_SameTimestamp_DeterministicBySeq()
    {
        var pkgA = new AgreementPackage
        {
            AgreementId = "AGR-OOO",
            Parties = new List<Party>
            {
                new() { Id = "PTY-A", Name = "Alice" },
                new() { Id = "PTY-B", Name = "Bob" }
            },
            Clauses = new List<Clause>
            {
                new() { Id = "CL-01", Obligor = "PTY-A", AmountFen = 1000, Due = "2026-09-01" }
            },
            Amendments = new List<Amendment>
            {
                new() { Id = "AMD-01", Replaces = "CL-01", NewClauseId = "CL-01-R1", AmountFen = 900 }
            },
            Signatures = new List<Signature>
            {
                new() { Id = "S1", PartyId = "PTY-B", Scope = new List<string> { "AGR-OOO" }, SignedAt = "2026-08-01T10:00:00Z", Seq = 2 },
                new() { Id = "S2", PartyId = "PTY-A", Scope = new List<string> { "AGR-OOO", "AMD-01" }, SignedAt = "2026-08-01T10:00:00Z", Seq = 1 }
            }
        };

        var pkgB = new AgreementPackage
        {
            AgreementId = "AGR-OOO",
            Parties = pkgA.Parties.ToList(),
            Clauses = pkgA.Clauses.ToList(),
            Amendments = pkgA.Amendments.ToList(),
            Signatures = new List<Signature>
            {
                new() { Id = "S2", PartyId = "PTY-A", Scope = new List<string> { "AGR-OOO", "AMD-01" }, SignedAt = "2026-08-01T10:00:00Z", Seq = 1 },
                new() { Id = "S1", PartyId = "PTY-B", Scope = new List<string> { "AGR-OOO" }, SignedAt = "2026-08-01T10:00:00Z", Seq = 2 }
            }
        };

        var rA = AgreementAuditor.AuditJson(Serialize(pkgA));
        var rB = AgreementAuditor.AuditJson(Serialize(pkgB));

        Assert.Equal(rA.Issues.Count, rB.Issues.Count);
        for (var i = 0; i < rA.Issues.Count; i++)
        {
            Assert.Equal(rA.Issues[i].Code, rB.Issues[i].Code);
        }
        Assert.False(rA.HasErrors);
        Assert.False(rB.HasErrors);
    }

    [Fact]
    public void WithdrawUnknownTarget_ReportsUnknown()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment
        {
            Id = "AMD-W", Withdraws = "NOPE-404"
        });

        var result = AgreementAuditor.AuditJson(Serialize(pkg));
        Assert.Contains(result.Issues, i => i.Code == IssueCodes.AmendmentWithdrawUnknown);
    }

    [Fact]
    public void DuplicateSubmission_WithWithdrawals_ProducesIdenticalResults()
    {
        var pkg = BasePackage();
        pkg.Amendments.Add(new Amendment { Id = "AMD-W", Withdraws = "AMD-01" });
        pkg.Amendments.Add(new Amendment { Id = "AMD-KILL", Withdraws = "AGR-WD" });

        var json = Serialize(pkg);
        var r1 = AgreementAuditor.AuditJson(json);
        var r2 = AgreementAuditor.AuditJson(json);

        Assert.Equal(r1.Issues.Count, r2.Issues.Count);
        for (var i = 0; i < r1.Issues.Count; i++)
        {
            Assert.Equal(r1.Issues[i].Code, r2.Issues[i].Code);
            Assert.Equal(r1.Issues[i].EvidencePath, r2.Issues[i].EvidencePath);
        }
        Assert.Equal(r1.AgreementWithdrawn, r2.AgreementWithdrawn);
    }

    [Fact]
    public void DeepChainWithWithdrawal_NoStackOverflow_Deterministic()
    {
        var pkg = BasePackage();
        pkg.Clauses.Clear();
        pkg.Amendments.Clear();
        pkg.Signatures.Clear();

        const int n = 5000;
        for (var i = 0; i < n; i++)
        {
            pkg.Clauses.Add(new Clause
            {
                Id = $"CL-{i:D5}", Obligor = "PTY-A", AmountFen = 100, Due = "2026-09-01",
                References = i + 1 < n ? new List<string> { $"CL-{i + 1:D5}" } : new List<string>()
            });
        }
        for (var i = 0; i < n; i++)
        {
            var target = i == 0 ? "CL-00000" : $"CL-V{i:D5}";
            pkg.Amendments.Add(new Amendment
            {
                Id = $"AMD-{i:D5}", Replaces = target, NewClauseId = $"CL-V{i + 1:D5}",
                AmountFen = 100 - (i % 50)
            });
        }
        pkg.Amendments.Add(new Amendment { Id = "AMD-W-LAST", Withdraws = $"AMD-{n - 1:D5}" });

        pkg.Signatures.Add(new Signature
        {
            Id = "S-A", PartyId = "PTY-A",
            Scope = new List<string> { "AGR-WD" }.Concat(Enumerable.Range(0, n).Select(i => $"AMD-{i:D5}")).ToList(),
            SignedAt = "2026-08-01T10:00:00Z"
        });
        pkg.Signatures.Add(new Signature
        {
            Id = "S-B", PartyId = "PTY-B",
            Scope = new List<string> { "AGR-WD" },
            SignedAt = "2026-08-01T10:01:00Z"
        });

        var json = Serialize(pkg);
        var r1 = AgreementAuditor.AuditJson(json);
        var r2 = AgreementAuditor.AuditJson(json);

        Assert.Equal(r1.Issues.Count, r2.Issues.Count);
        for (var i = 0; i < r1.Issues.Count; i++)
        {
            Assert.Equal(r1.Issues[i].Code, r2.Issues[i].Code);
        }
    }

    [Fact]
    public void FieldReorder_WithWithdrawals_SameCodesAndResult()
    {
        var pkgA = BasePackage();
        pkgA.Amendments.Add(new Amendment { Id = "AMD-W", Withdraws = "AMD-01" });
        pkgA.Amendments.Add(new Amendment { Id = "AMD-RM", Withdraws = "PTY-B" });

        var pkgB = new AgreementPackage
        {
            AgreementId = "AGR-WD",
            Signatures = new List<Signature>
            {
                new() { SignedAt = "2026-08-01T10:02:00Z", Seq = 2, Scope = new List<string> { "AGR-WD" }, PartyId = "PTY-B", Id = "SIG-B" },
                new() { SignedAt = "2026-08-01T10:00:00Z", Seq = 1, Scope = new List<string> { "AMD-01", "AGR-WD" }, PartyId = "PTY-A", Id = "SIG-A" }
            },
            Amendments = new List<Amendment>
            {
                new() { Withdraws = "PTY-B", Id = "AMD-RM" },
                new() { Withdraws = "AMD-01", Id = "AMD-W" },
                new() { Due = "2026-10-01", AmountFen = 8000, NewClauseId = "CL-01-R1", Replaces = "CL-01", Id = "AMD-01" }
            },
            Clauses = new List<Clause>
            {
                new() { Due = "2026-09-01", AmountFen = 10000, Obligor = "PTY-A", Id = "CL-01", References = new List<string>() }
            },
            Parties = new List<Party>
            {
                new() { Name = "Bob", Id = "PTY-B" },
                new() { Name = "Alice", Id = "PTY-A" }
            }
        };

        var rA = AgreementAuditor.AuditJson(Serialize(pkgA));
        var rB = AgreementAuditor.AuditJson(Serialize(pkgB));

        var codesA = rA.Issues.Select(i => i.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var codesB = rB.Issues.Select(i => i.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.Equal(codesA, codesB);
        Assert.Equal(rA.AgreementWithdrawn, rB.AgreementWithdrawn);
        Assert.Equal(
            rA.RemovedParties.OrderBy(p => p, StringComparer.Ordinal),
            rB.RemovedParties.OrderBy(p => p, StringComparer.Ordinal));
    }
}
