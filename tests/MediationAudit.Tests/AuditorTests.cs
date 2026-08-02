using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MediationAudit.Tests;

/// <summary>
/// Verifies that each stable issue code fires on its dedicated fixture and that the clean fixture
/// stays silent.
/// </summary>
public sealed class IssueCodeTests
{
    private static AuditResult Audit(string json) => new AgreementAuditor().AuditJson(json);

    private static bool Has(AuditResult result, string code) =>
        result.Issues.Any(i => i.Code == code);

    [Fact]
    public void Clean_fixture_yields_no_issues()
    {
        var result = Audit(Fixtures.Clean);
        Assert.Empty(result.Issues);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Unknown_reference_is_flagged()
    {
        var result = Audit(Fixtures.UnknownReference);
        Assert.True(Has(result, MediationIssueCodes.UnknownReference));
        Assert.Equal("$.clauses[0].references[0]",
            result.Issues.Single(i => i.Code == MediationIssueCodes.UnknownReference).EvidencePath);
    }

    [Fact]
    public void Amendment_target_missing_is_flagged()
    {
        var result = Audit(Fixtures.AmendmentTargetMissing);
        Assert.True(Has(result, MediationIssueCodes.AmendmentTargetMissing));
        Assert.Equal("$.amendments[0].replaces",
            result.Issues.Single(i => i.Code == MediationIssueCodes.AmendmentTargetMissing).EvidencePath);
    }

    [Fact]
    public void Duplicate_replacement_is_flagged()
    {
        var result = Audit(Fixtures.DuplicateReplacement);
        Assert.True(Has(result, MediationIssueCodes.DuplicateReplacement));
        // The later amendment (index 1) is the one reported as the ambiguous duplicate.
        Assert.Equal("$.amendments[1].replaces",
            result.Issues.Single(i => i.Code == MediationIssueCodes.DuplicateReplacement).EvidencePath);
    }

    [Fact]
    public void Amount_inconsistent_is_flagged()
    {
        var result = Audit(Fixtures.AmountInconsistent);
        Assert.True(Has(result, MediationIssueCodes.AmountInconsistent));
        Assert.Equal("$.amendments[1].amountFen",
            result.Issues.Single(i => i.Code == MediationIssueCodes.AmountInconsistent).EvidencePath);
    }

    [Fact]
    public void Signature_scope_gap_is_flagged()
    {
        var result = Audit(Fixtures.SignatureScopeGap);
        Assert.True(Has(result, MediationIssueCodes.SignatureScopeGap));
        // PTY-B's first (only) signature is at signatures index 1.
        Assert.Equal("$.signatures[1].scope",
            result.Issues.Single(i => i.Code == MediationIssueCodes.SignatureScopeGap).EvidencePath);
    }

    [Fact]
    public void Date_order_violation_is_flagged()
    {
        var result = Audit(Fixtures.DateOrderViolation);
        Assert.True(Has(result, MediationIssueCodes.DateOrderViolation));
        Assert.Equal("$.clauses[1].references[0]",
            result.Issues.Single(i => i.Code == MediationIssueCodes.DateOrderViolation).EvidencePath);
    }

    [Fact]
    public void Reference_cycle_is_flagged()
    {
        var result = Audit(Fixtures.ReferenceCycle);
        Assert.True(Has(result, MediationIssueCodes.ReferenceCycle));
        // Canonical representative is the lowest clause id (CL-01) at clauses index 0.
        Assert.Equal("$.clauses[0].references",
            result.Issues.Single(i => i.Code == MediationIssueCodes.ReferenceCycle).EvidencePath);
    }

    [Fact]
    public void Withdrawn_amendment_reference_is_flagged()
    {
        var result = Audit(Fixtures.WithdrawnAmendmentReferenced);
        Assert.True(Has(result, MediationIssueCodes.WithdrawnAmendmentReferenced));
        Assert.Equal("$.signatures[0].scope[1]",
            result.Issues.Single(i => i.Code == MediationIssueCodes.WithdrawnAmendmentReferenced).EvidencePath);
    }

    [Fact]
    public void Every_issue_code_uses_the_MED_prefix()
    {
        foreach (var code in AllCodes())
            Assert.StartsWith("MED_", code);
    }

    private static IEnumerable<string> AllCodes() => new[]
    {
        MediationIssueCodes.UnknownReference,
        MediationIssueCodes.AmendmentTargetMissing,
        MediationIssueCodes.DuplicateReplacement,
        MediationIssueCodes.AmountInconsistent,
        MediationIssueCodes.SignatureScopeGap,
        MediationIssueCodes.DateOrderViolation,
        MediationIssueCodes.ReferenceCycle,
        MediationIssueCodes.WithdrawnAmendmentReferenced,
    };
}

/// <summary>
/// Determinism and robustness properties: field-order independence, traversal-order independence,
/// idempotent re-submission, evidence paths anchored in raw JSON, and safety on hostile depth.
/// </summary>
public sealed class DeterminismTests
{
    private static AuditResult Audit(string json) => new AgreementAuditor().AuditJson(json);

    private static string Signature(AuditResult result) =>
        string.Join("\n", result.Issues.Select(i => $"{i.Code}|{i.EvidencePath}|{i.Severity}"));

    public static IEnumerable<object[]> AllFixtures() => new[]
    {
        new object[] { Fixtures.Clean },
        new object[] { Fixtures.UnknownReference },
        new object[] { Fixtures.AmendmentTargetMissing },
        new object[] { Fixtures.DuplicateReplacement },
        new object[] { Fixtures.AmountInconsistent },
        new object[] { Fixtures.SignatureScopeGap },
        new object[] { Fixtures.DateOrderViolation },
        new object[] { Fixtures.ReferenceCycle },
        new object[] { Fixtures.WithdrawnAmendmentReferenced },
    };

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Resubmitting_the_same_package_is_idempotent(string json)
    {
        Assert.Equal(Signature(Audit(json)), Signature(Audit(json)));
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Reordering_json_fields_does_not_change_findings(string json)
    {
        var baseline = Signature(Audit(json));
        for (int seed = 0; seed < 8; seed++)
        {
            string shuffled = JsonFieldShuffler.Shuffle(json, seed);
            // Element order within arrays is preserved (indices matter); only object key order changes.
            Assert.Equal(baseline, Signature(Audit(shuffled)));
        }
    }

    [Fact]
    public void Evidence_paths_resolve_against_the_raw_json()
    {
        foreach (var fixture in AllFixtures().Select(f => (string)f[0]))
        {
            using var document = JsonDocument.Parse(fixture);
            var result = Audit(fixture);
            foreach (var issue in result.Issues)
                Assert.True(EvidencePathResolver.Exists(document.RootElement, issue.EvidencePath),
                    $"Evidence path '{issue.EvidencePath}' did not resolve in the source JSON.");
        }
    }

    [Fact]
    public void Random_graphs_produce_stable_traversal_independent_output()
    {
        for (int seed = 0; seed < 60; seed++)
        {
            string json = RandomAgreementFactory.Build(seed, out string shuffled);

            var a = Audit(json);
            var b = Audit(shuffled);

            // Same package, different field order and array-independent shuffles => identical output.
            Assert.Equal(Signature(a), Signature(b));

            // Output is already sorted by stable identity (code + position-independent sort key),
            // independent of discovery order.
            var resorted = a.Issues
                .OrderBy(i => i.Code, StringComparer.Ordinal)
                .ThenBy(i => i.SortKey, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(Signature(a), string.Join("\n",
                resorted.Select(i => $"{i.Code}|{i.EvidencePath}|{i.Severity}")));

            // Every evidence path must resolve against the raw document.
            using var document = JsonDocument.Parse(json);
            foreach (var issue in a.Issues)
                Assert.True(EvidencePathResolver.Exists(document.RootElement, issue.EvidencePath));
        }
    }

    [Fact]
    public void Deep_linear_reference_chain_does_not_overflow_and_reports_no_cycle()
    {
        const int depth = 50_000;
        string json = DeepChainFactory.LinearChain(depth);

        var result = Audit(json);

        // A pure chain has no cycle, and every reference resolves, so there are no findings.
        Assert.DoesNotContain(result.Issues, i => i.Code == MediationIssueCodes.ReferenceCycle);
        Assert.DoesNotContain(result.Issues, i => i.Code == MediationIssueCodes.UnknownReference);
    }

    [Fact]
    public void Deep_cycle_is_detected_without_overflow()
    {
        const int depth = 50_000;
        string json = DeepChainFactory.CyclicChain(depth);

        var result = Audit(json);
        Assert.Contains(result.Issues, i => i.Code == MediationIssueCodes.ReferenceCycle);
    }

    [Fact]
    public void Hostile_deeply_nested_json_is_rejected_not_crashed()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < 20_000; i++) builder.Append('[');
        for (int i = 0; i < 20_000; i++) builder.Append(']');

        // System.Text.Json enforces a max depth, so this surfaces as a parse exception, never a crash.
        Assert.Throws<AgreementParseException>(() => new AgreementAuditor().AuditJson(builder.ToString()));
    }
}

/// <summary>Reorders JSON object keys deterministically while preserving array element order.</summary>
internal static class JsonFieldShuffler
{
    public static string Shuffle(string json, int seed)
    {
        using var document = JsonDocument.Parse(json);
        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteShuffled(document.RootElement, writer, new Random(seed));
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteShuffled(JsonElement element, Utf8JsonWriter writer, Random random)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().ToList();
                for (int i = properties.Count - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (properties[i], properties[j]) = (properties[j], properties[i]);
                }
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteShuffled(property.Value, writer, random);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteShuffled(item, writer, random); // order preserved on purpose
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

/// <summary>Resolves the subset of JSONPath the auditor emits (<c>$.array[i].field</c> and nested indices).</summary>
internal static class EvidencePathResolver
{
    public static bool Exists(JsonElement root, string path)
    {
        if (!path.StartsWith("$", StringComparison.Ordinal)) return false;
        JsonElement current = root;
        int i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                i++;
                int start = i;
                while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
                string name = path.Substring(start, i - start);
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                    return false;
            }
            else if (path[i] == '[')
            {
                int end = path.IndexOf(']', i);
                if (end < 0) return false;
                int index = int.Parse(path.Substring(i + 1, end - i - 1));
                if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength())
                    return false;
                current = current[index];
                i = end + 1;
            }
            else
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>Builds pseudo-random agreement packages and a field-shuffled twin for determinism tests.</summary>
internal static class RandomAgreementFactory
{
    public static string Build(int seed, out string shuffledTwin)
    {
        var random = new Random(seed);
        int partyCount = 2 + random.Next(3);
        int clauseCount = 2 + random.Next(6);

        var parties = new List<string>();
        for (int i = 0; i < partyCount; i++) parties.Add($"PTY-{(char)('A' + i)}");

        var clauseIds = new List<string>();
        for (int i = 0; i < clauseCount; i++) clauseIds.Add($"CL-{i:00}");

        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"agreementId\":\"AGR-RND\",");

        sb.Append("\"parties\":[");
        for (int i = 0; i < parties.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"id\":\"{parties[i]}\",\"name\":\"N{i}\"}}");
        }
        sb.Append("],");

        sb.Append("\"clauses\":[");
        for (int i = 0; i < clauseCount; i++)
        {
            if (i > 0) sb.Append(',');
            var refs = new List<string>();
            int refCount = random.Next(3);
            for (int r = 0; r < refCount; r++)
            {
                // Occasionally emit a dangling reference to exercise MED_UNKNOWN_REFERENCE.
                if (random.Next(4) == 0) refs.Add($"CL-{99 + r}");
                else refs.Add(clauseIds[random.Next(clauseCount)]);
            }
            int month = 8 + random.Next(4);
            sb.Append($"{{\"id\":\"{clauseIds[i]}\",\"obligor\":\"{parties[random.Next(parties.Count)]}\",");
            sb.Append($"\"amountFen\":{random.Next(1, 5000) * 100},\"due\":\"2026-{month:00}-01\",");
            sb.Append("\"references\":[");
            for (int r = 0; r < refs.Count; r++)
            {
                if (r > 0) sb.Append(',');
                sb.Append($"\"{refs[r]}\"");
            }
            sb.Append("]}");
        }
        sb.Append("],");

        sb.Append("\"amendments\":[");
        int amendmentCount = random.Next(4);
        for (int i = 0; i < amendmentCount; i++)
        {
            if (i > 0) sb.Append(',');
            string replaces = random.Next(3) == 0 ? "CL-404" : clauseIds[random.Next(clauseCount)];
            bool withdrawn = random.Next(4) == 0;
            sb.Append($"{{\"id\":\"AMD-{i:00}\",\"replaces\":\"{replaces}\",\"newClauseId\":\"CL-R{i}\",");
            sb.Append($"\"amountFen\":{random.Next(1, 5000) * 100},\"withdrawn\":{(withdrawn ? "true" : "false")}}}");
        }
        sb.Append("],");

        sb.Append("\"signatures\":[");
        for (int i = 0; i < parties.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"partyId\":\"{parties[i]}\",\"scope\":[\"AGR-RND\"],\"signedAt\":\"2026-08-01T10:0{i}:00Z\"}}");
        }
        sb.Append("]}");

        string json = sb.ToString();
        shuffledTwin = JsonFieldShuffler.Shuffle(json, seed + 1000);
        return json;
    }
}

/// <summary>Builds pathologically deep reference chains to prove the auditor never recurses on graph depth.</summary>
internal static class DeepChainFactory
{
    /// <summary>CL-0 -> CL-1 -> ... -> CL-(n-1), acyclic.</summary>
    public static string LinearChain(int depth) => Build(depth, cyclic: false);

    /// <summary>A chain whose last node references the first, forming one large cycle.</summary>
    public static string CyclicChain(int depth) => Build(depth, cyclic: true);

    private static string Build(int depth, bool cyclic)
    {
        var sb = new StringBuilder();
        sb.Append("{\"agreementId\":\"AGR-DEEP\",");
        sb.Append("\"parties\":[{\"id\":\"PTY-A\",\"name\":\"A\"}],");
        sb.Append("\"clauses\":[");
        for (int i = 0; i < depth; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"id\":\"CL-{i}\",\"obligor\":\"PTY-A\",\"references\":[");
            if (i < depth - 1) sb.Append($"\"CL-{i + 1}\"");
            else if (cyclic) sb.Append("\"CL-0\"");
            sb.Append("]}");
        }
        sb.Append("],\"amendments\":[],");
        sb.Append("\"signatures\":[{\"partyId\":\"PTY-A\",\"scope\":[\"AGR-DEEP\"],\"signedAt\":\"2026-08-01T10:00:00Z\"}]}");
        return sb.ToString();
    }
}
