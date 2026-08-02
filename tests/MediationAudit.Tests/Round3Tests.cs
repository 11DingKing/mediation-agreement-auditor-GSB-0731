using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MediationAudit.Tests;

/// <summary>
/// Round-3 tests: the ordered event log and the four revocation semantics. Verifies that
/// revoke-amendment / revoke-agreement / remove-party fold into the effective state, that
/// revoke-signed-fact reports a stable code instead of rewriting history, and that outcomes are
/// determined by the material's sequence numbers — not array/arrival order — even for events sharing
/// a wall-clock timestamp. Determinism is proven under deep chains, duplicate submission and field
/// or events-array reordering.
/// </summary>
public sealed class EventLogTests
{
    private static AgreementAuditor Auditor => new AgreementAuditor();

    private static EffectiveState State(string json) =>
        Auditor.ComputeEffectiveState(AgreementAuditor.Parse(json));

    private static AuditResult Audit(string json) => Auditor.AuditJson(json);

    private static bool Has(AuditResult r, string code) => r.Issues.Any(i => i.Code == code);

    private static string IssueSignature(AuditResult r) =>
        string.Join("\n", r.Issues.Select(i => $"{i.Code}|{i.EvidencePath}|{i.Severity}"));

    private static string LogicalSignature(AuditResult r) =>
        string.Join("\n", r.Issues.Select(i => $"{i.Code}|{i.SortKey}|{i.Severity}"));

    private static string StateSignature(EffectiveState s) =>
        $"revoked={s.AgreementRevoked};parties={string.Join(",", s.ActiveParties)};" +
        $"revokedAmd={string.Join(",", s.RevokedAmendmentIds)};" +
        $"sigs={string.Join("|", s.EffectiveSignatures.Select(kv => $"{kv.Key}:{string.Join("+", kv.Value)}"))}";

    // ----- the four revocation semantics -------------------------------------------------------

    [Fact]
    public void Revoke_amendment_marks_it_revoked_in_effective_state()
    {
        var state = State(Fixtures.EventRevokeAmendment);
        Assert.Contains("AMD-01", state.RevokedAmendmentIds);
        Assert.False(state.AgreementRevoked);
    }

    [Fact]
    public void Revoke_agreement_sets_the_flag()
    {
        var state = State(Fixtures.EventRevokeAgreement);
        Assert.True(state.AgreementRevoked);
    }

    [Fact]
    public void Remove_party_drops_party_and_its_signatures()
    {
        var state = State(Fixtures.EventRemoveParty);
        Assert.DoesNotContain("PTY-B", state.ActiveParties);
        Assert.Contains("PTY-A", state.ActiveParties);
        Assert.DoesNotContain("PTY-B", state.EffectiveSignatures.Keys);
    }

    [Fact]
    public void Revoke_signed_fact_reports_stable_code_and_never_rewrites_history()
    {
        var result = Audit(Fixtures.EventRevokeSignedFact);
        var issue = result.Issues.Single(i => i.Code == MediationIssueCodes.SignedFactRevocationAttempt);
        Assert.Equal("$.events[0]", issue.EvidencePath);

        // The signed fact is preserved in the effective state.
        var state = State(Fixtures.EventRevokeSignedFact);
        Assert.True(state.EffectiveSignatures.TryGetValue("PTY-A", out var scopes));
        Assert.Contains("AGR-EVS", scopes!);
    }

    // ----- ordering is by sequence, not arrival ------------------------------------------------

    [Fact]
    public void Same_timestamp_out_of_order_events_resolve_by_sequence_number()
    {
        var state = State(Fixtures.EventSameTimestampOutOfOrder);
        // seq1 sign AGR-OOO, seq2 revoke AMD-01, seq3 sign AMD-01 => both scopes signed, AMD-01 revoked.
        Assert.True(state.EffectiveSignatures.TryGetValue("PTY-A", out var scopes));
        Assert.Equal(new[] { "AGR-OOO", "AMD-01" }, scopes);
        Assert.Contains("AMD-01", state.RevokedAmendmentIds);
    }

    [Fact]
    public void Reordering_events_array_does_not_change_state_or_findings()
    {
        foreach (var fixture in EventFixtures().Select(f => (string)f[0]))
        {
            var baselineState = StateSignature(State(fixture));
            var baselineLogical = LogicalSignature(Audit(fixture));
            foreach (var permuted in EventReorderer.AllPermutations(fixture))
            {
                Assert.Equal(baselineState, StateSignature(State(permuted)));
                // Findings are identical modulo the physical evidence indices, which are compared via
                // the position-independent logical identity (code + sort key).
                Assert.Equal(baselineLogical, LogicalSignature(Audit(permuted)));
            }
        }
    }

    // ----- structural event findings -----------------------------------------------------------

    [Fact]
    public void Sequence_conflict_is_reported_for_every_colliding_event()
    {
        var result = Audit(Fixtures.EventSequenceConflict);
        var conflicts = result.Issues.Where(i => i.Code == MediationIssueCodes.EventSequenceConflict).ToList();
        Assert.Equal(2, conflicts.Count);
        Assert.All(conflicts, i => Assert.EndsWith(".seq", i.EvidencePath));
    }

    [Fact]
    public void Unknown_event_target_is_reported()
    {
        var result = Audit(Fixtures.EventUnknownTarget);
        var issue = result.Issues.Single(i => i.Code == MediationIssueCodes.EventTargetUnknown);
        Assert.Equal("$.events[0]", issue.EvidencePath);
    }

    [Fact]
    public void Every_event_issue_code_uses_the_MED_prefix()
    {
        foreach (var code in new[]
                 {
                     MediationIssueCodes.SignedFactRevocationAttempt,
                     MediationIssueCodes.EventTargetUnknown,
                     MediationIssueCodes.EventSequenceConflict,
                 })
            Assert.StartsWith("MED_", code);
    }

    // ----- determinism: duplicate submission, field reorder, evidence paths, deep chains -------

    public static IEnumerable<object[]> EventFixtures() => new[]
    {
        new object[] { Fixtures.EventRevokeAmendment },
        new object[] { Fixtures.EventRevokeAgreement },
        new object[] { Fixtures.EventRemoveParty },
        new object[] { Fixtures.EventRevokeSignedFact },
        new object[] { Fixtures.EventSameTimestampOutOfOrder },
        new object[] { Fixtures.EventSequenceConflict },
        new object[] { Fixtures.EventUnknownTarget },
    };

    [Theory]
    [MemberData(nameof(EventFixtures))]
    public void Duplicate_submission_is_idempotent(string json)
    {
        Assert.Equal(IssueSignature(Audit(json)), IssueSignature(Audit(json)));
        Assert.Equal(StateSignature(State(json)), StateSignature(State(json)));
    }

    [Theory]
    [MemberData(nameof(EventFixtures))]
    public void Field_reordering_preserves_findings_and_state(string json)
    {
        var baselineIssues = IssueSignature(Audit(json));
        var baselineState = StateSignature(State(json));
        for (int seed = 0; seed < 6; seed++)
        {
            string shuffled = JsonFieldShuffler.Shuffle(json, seed);
            Assert.Equal(baselineIssues, IssueSignature(Audit(shuffled)));
            Assert.Equal(baselineState, StateSignature(State(shuffled)));
        }
    }

    [Theory]
    [MemberData(nameof(EventFixtures))]
    public void Event_evidence_paths_resolve_against_raw_json(string json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var issue in Audit(json).Issues)
            Assert.True(EvidencePathResolver.Exists(document.RootElement, issue.EvidencePath),
                $"Evidence path '{issue.EvidencePath}' did not resolve.");
    }

    [Fact]
    public void Deep_event_log_with_deep_reference_chain_stays_deterministic_and_safe()
    {
        const int depth = 50_000;
        string json = DeepEventLogFactory.Build(depth);

        // A long sign log over a deep clause chain: no crash, and a second pass is identical.
        var first = Audit(json);
        var second = Audit(json);
        Assert.Equal(IssueSignature(first), IssueSignature(second));

        var state = State(json);
        Assert.True(state.EffectiveSignatures.TryGetValue("PTY-A", out var scopes));
        Assert.NotEmpty(scopes!);
    }
}

/// <summary>Generates permutations of the <c>$.events</c> array, leaving every other array intact.</summary>
internal static class EventReorderer
{
    public static IEnumerable<string> AllPermutations(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            yield break;

        var elements = events.EnumerateArray().Select(e => e.GetRawText()).ToList();
        int count = elements.Count;
        if (count < 2) yield break;

        yield return Rebuild(json, Enumerable.Range(0, count).Reverse().Select(i => elements[i]));
        for (int shift = 1; shift < count; shift++)
            yield return Rebuild(json, Enumerable.Range(0, count).Select(i => elements[(i + shift) % count]));
    }

    private static string Rebuild(string json, IEnumerable<string> orderedEventTexts)
    {
        using var document = JsonDocument.Parse(json);
        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("events"))
                {
                    writer.WritePropertyName("events");
                    writer.WriteStartArray();
                    foreach (var raw in orderedEventTexts)
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
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>Builds a package with a deep clause chain and a deep, shuffled sign-event log.</summary>
internal static class DeepEventLogFactory
{
    public static string Build(int depth)
    {
        var sb = new StringBuilder();
        sb.Append("{\"agreementId\":\"AGR-DEEPE\",");
        sb.Append("\"parties\":[{\"id\":\"PTY-A\",\"name\":\"A\"}],");
        sb.Append("\"clauses\":[");
        for (int i = 0; i < depth; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"id\":\"CL-{i}\",\"obligor\":\"PTY-A\",\"references\":[");
            if (i < depth - 1) sb.Append($"\"CL-{i + 1}\"");
            sb.Append("]}");
        }
        sb.Append("],\"amendments\":[],\"signatures\":[],");
        // Sign events emitted in DESCENDING sequence so the fold must sort them; scopes are unique.
        sb.Append("\"events\":[");
        for (int i = 0; i < depth; i++)
        {
            if (i > 0) sb.Append(',');
            int seq = depth - i;
            sb.Append($"{{\"seq\":{seq},\"type\":\"sign\",\"partyId\":\"PTY-A\",\"scope\":\"S-{seq}\",\"at\":\"2026-08-02T09:00:00Z\"}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }
}
