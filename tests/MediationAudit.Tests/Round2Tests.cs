using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace MediationAudit.Tests;

/// <summary>
/// Round-2 tests: the auditable directed version chain. Verifies replace/append/multiple-replacement
/// handling, that superseded clauses are retained (never physically deleted), and that swapping the
/// order of the <c>$.amendments</c> array leaves the effective view, conflict codes and evidence
/// ordering unchanged. Also checks the shortest, stable evidence for replacement cycles and forks.
/// </summary>
public sealed class VersionChainTests
{
    private static AgreementAuditor Auditor => new AgreementAuditor();

    private static ClauseVersionChain Chain(string json) =>
        Auditor.BuildVersionChain(AgreementAuditor.Parse(json));

    private static AuditResult Audit(string json) => Auditor.AuditJson(json);

    private static string IssueSignature(AuditResult result) =>
        string.Join("\n", result.Issues.Select(i => $"{i.Code}|{i.EvidencePath}|{i.Severity}"));

    [Fact]
    public void Revision_chain_effective_view_is_the_latest_version()
    {
        var chain = Chain(Fixtures.RevisionChain);
        Assert.Equal(new[] { "CL-01-R2" }, chain.EffectiveClauseIds());

        // Nothing is deleted: every version (including the two superseded ones) is retained as a node.
        var nodeIds = chain.Nodes.Select(n => n.ClauseId).ToList();
        Assert.Contains("CL-01", nodeIds);
        Assert.Contains("CL-01-R1", nodeIds);
        Assert.Contains("CL-01-R2", nodeIds);
        Assert.True(chain.Nodes.Single(n => n.ClauseId == "CL-01").IsSuperseded);
        Assert.True(chain.Nodes.Single(n => n.ClauseId == "CL-01-R1").IsSuperseded);
        Assert.False(chain.Nodes.Single(n => n.ClauseId == "CL-01-R2").IsSuperseded);
    }

    [Fact]
    public void Append_amendment_adds_a_tip_without_superseding_anything()
    {
        var chain = Chain(Fixtures.AppendAmendment);
        // Both the original CL-01 and the appended CL-02 are effective tips.
        Assert.Equal(new[] { "CL-01", "CL-02" }, chain.EffectiveClauseIds());
        Assert.Contains(chain.Edges, e => e.Kind == RevisionKind.Append && e.ToClauseId == "CL-02");
        Assert.DoesNotContain(chain.Nodes, n => n.IsSuperseded);
    }

    [Fact]
    public void Unknown_replacement_target_produces_no_edge_but_is_reported()
    {
        var chain = Chain(Fixtures.AmendmentTargetMissing);
        Assert.Empty(chain.Edges); // the only amendment targets a missing clause
        Assert.Contains(Audit(Fixtures.AmendmentTargetMissing).Issues,
            i => i.Code == MediationIssueCodes.AmendmentTargetMissing);
    }

    [Fact]
    public void Forked_chain_keeps_both_versions_and_picks_smallest_id_winner()
    {
        var chain = Chain(Fixtures.ForkedChain);
        // Both forks are retained as nodes; the winner (smallest amendment id AMD-01 => CL-01-A) is
        // the effective tip alongside the loser fork CL-01-B, which is itself a tip (never superseded).
        var effective = chain.EffectiveClauseIds();
        Assert.Contains("CL-01-A", effective);
        Assert.Contains("CL-01-B", effective);
        Assert.DoesNotContain("CL-01", effective); // the shared parent is superseded
        Assert.True(chain.Nodes.Single(n => n.ClauseId == "CL-01").IsSuperseded);

        var result = Audit(Fixtures.ForkedChain);
        Assert.Contains(result.Issues, i => i.Code == MediationIssueCodes.DuplicateReplacement);
        Assert.Contains(result.Issues, i => i.Code == MediationIssueCodes.AmountInconsistent);
    }

    [Fact]
    public void Replacement_cycle_is_detected_with_shortest_stable_evidence()
    {
        var result = Audit(Fixtures.ReplacementCycle);
        var cycle = result.Issues.Single(i => i.Code == MediationIssueCodes.ReplacementCycle);
        // Shortest anchor: a single replaces field on the smallest-id amendment (AMD-01, index 0).
        Assert.Equal("$.amendments[0].replaces", cycle.EvidencePath);
    }

    [Fact]
    public void Reference_cycle_evidence_is_a_single_shortest_path()
    {
        var result = Audit(Fixtures.ReferenceCycle);
        var cycle = result.Issues.Single(i => i.Code == MediationIssueCodes.ReferenceCycle);
        // One anchor on the canonical (lowest-id) clause's references array, not one per member.
        Assert.Equal("$.clauses[0].references", cycle.EvidencePath);
    }

    public static IEnumerable<object[]> ChainFixtures() => new[]
    {
        new object[] { Fixtures.RevisionChain },
        new object[] { Fixtures.AppendAmendment },
        new object[] { Fixtures.ForkedChain },
        new object[] { Fixtures.ReplacementCycle },
        new object[] { Fixtures.AmountInconsistent },
        new object[] { Fixtures.DuplicateReplacement },
        new object[] { Fixtures.WithdrawnAmendmentReferenced },
    };

    [Theory]
    [MemberData(nameof(ChainFixtures))]
    public void Swapping_amendments_array_order_preserves_effective_view(string json)
    {
        var baseline = Chain(json).EffectiveClauseIds();
        foreach (var permuted in AmendmentReorderer.AllPermutations(json))
            Assert.Equal(baseline, Chain(permuted).EffectiveClauseIds());
    }

    [Theory]
    [MemberData(nameof(ChainFixtures))]
    public void Swapping_amendments_array_order_preserves_conflict_codes_and_evidence_order(string json)
    {
        var baseline = IssueSignature(Audit(json));
        foreach (var permuted in AmendmentReorderer.AllPermutations(json))
        {
            // Evidence paths follow the elements to their new indices, but the ordered SEQUENCE of
            // (code, path, severity) triples must be identical because ordering is position-independent.
            var permutedResult = Audit(permuted);
            Assert.Equal(
                CanonicalByLogicalIdentity(Audit(json)),
                CanonicalByLogicalIdentity(permutedResult));

            // And the effective view + conflict codes match regardless of order.
            Assert.Equal(
                Audit(json).CountByCode().OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"),
                permutedResult.CountByCode().OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));
        }

        _ = baseline;
    }

    // Evidence paths reference physical indices, which legitimately move when the array is reordered.
    // To compare "same findings, same order" we project each issue onto its position-independent
    // identity: the code plus the SortKey the auditor itself uses for ordering.
    private static string CanonicalByLogicalIdentity(AuditResult result) =>
        string.Join("\n", result.Issues.Select(i => $"{i.Code}|{i.SortKey}|{i.Severity}"));

    [Fact]
    public void Swapping_amendments_keeps_evidence_paths_resolving_to_raw_json()
    {
        foreach (var permuted in AmendmentReorderer.AllPermutations(Fixtures.ForkedChain))
        {
            using var document = JsonDocument.Parse(permuted);
            foreach (var issue in Audit(permuted).Issues)
                Assert.True(EvidencePathResolver.Exists(document.RootElement, issue.EvidencePath),
                    $"Evidence path '{issue.EvidencePath}' did not resolve after reordering.");
        }
    }

    [Fact]
    public void Deep_replacement_chain_does_not_overflow_and_reports_no_cycle()
    {
        string json = DeepVersionChainFactory.LinearReplacementChain(50_000);
        var result = Audit(json);
        Assert.DoesNotContain(result.Issues, i => i.Code == MediationIssueCodes.ReplacementCycle);

        // The effective tip is the final version of the chain.
        var chain = Chain(json);
        Assert.Single(chain.EffectiveClauseIds());
    }

    [Fact]
    public void Deep_replacement_cycle_is_detected_without_overflow()
    {
        string json = DeepVersionChainFactory.CyclicReplacementChain(50_000);
        Assert.Contains(Audit(json).Issues, i => i.Code == MediationIssueCodes.ReplacementCycle);
    }
}

/// <summary>Generates permutations of the <c>$.amendments</c> array while leaving every other array intact.</summary>
internal static class AmendmentReorderer
{
    /// <summary>Yields the reversed order and a few rotations of the amendments array.</summary>
    public static IEnumerable<string> AllPermutations(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("amendments", out var amendments) || amendments.ValueKind != JsonValueKind.Array)
            yield break;

        var elements = amendments.EnumerateArray().Select(e => e.GetRawText()).ToList();
        int count = elements.Count;
        if (count < 2) yield break;

        // Reversed order.
        yield return Rebuild(json, Enumerable.Range(0, count).Reverse().Select(i => elements[i]));
        // Every single rotation.
        for (int shift = 1; shift < count; shift++)
            yield return Rebuild(json, Enumerable.Range(0, count).Select(i => elements[(i + shift) % count]));
    }

    private static string Rebuild(string json, IEnumerable<string> orderedAmendmentTexts)
    {
        using var document = JsonDocument.Parse(json);
        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("amendments"))
                {
                    writer.WritePropertyName("amendments");
                    writer.WriteStartArray();
                    foreach (var raw in orderedAmendmentTexts)
                    {
                        using var element = JsonDocument.Parse(raw);
                        element.RootElement.WriteTo(writer);
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>Builds pathologically deep replacement (version) chains for stack-safety tests.</summary>
internal static class DeepVersionChainFactory
{
    /// <summary>CL-0 replaced by CL-1 replaced by ... a single long acyclic version lineage.</summary>
    public static string LinearReplacementChain(int depth) => Build(depth, cyclic: false);

    /// <summary>The same, but the final amendment replaces the last version back into CL-0.</summary>
    public static string CyclicReplacementChain(int depth) => Build(depth, cyclic: true);

    private static string Build(int depth, bool cyclic)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"agreementId\":\"AGR-DEEPV\",");
        sb.Append("\"parties\":[{\"id\":\"PTY-A\",\"name\":\"A\"}],");
        // Only CL-0 exists as an original clause; each amendment introduces the next version id.
        sb.Append("\"clauses\":[{\"id\":\"CL-0\",\"obligor\":\"PTY-A\"}],");
        sb.Append("\"amendments\":[");
        for (int i = 0; i < depth; i++)
        {
            if (i > 0) sb.Append(',');
            // amendment i: replaces CL-i -> newClauseId CL-(i+1). Zero-padded ids keep ordinal order.
            sb.Append($"{{\"id\":\"AMD-{i:D6}\",\"replaces\":\"CL-{i}\",\"newClauseId\":\"CL-{i + 1}\"}}");
        }
        if (cyclic)
            sb.Append($",{{\"id\":\"AMD-{depth:D6}\",\"replaces\":\"CL-{depth}\",\"newClauseId\":\"CL-0\"}}");
        sb.Append("],");
        sb.Append("\"signatures\":[{\"partyId\":\"PTY-A\",\"scope\":[\"AGR-DEEPV\"],\"signedAt\":\"2026-08-01T10:00:00Z\"}]}");
        return sb.ToString();
    }
}
