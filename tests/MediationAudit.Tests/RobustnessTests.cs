using System.Text;
using System.Text.Json;
using MediationAudit;
using Xunit;

namespace MediationAudit.Tests;

/// <summary>
/// 健壮性测试：JSON 字段顺序、重复提交、超深引用链、随机协议图与恶意深度输入。
/// 核心不变量：结果确定性、问题代码稳定、证据路径始终可在原始 JSON 中解析、
/// 任意深度输入都不会造成栈溢出或非受控失败。
/// </summary>
public class RobustnessTests
{
    [Fact]
    public void FieldOrderChange_DoesNotAffectResult()
    {
        // 同一份协议的两种字段顺序写法，审计结果必须逐字节一致。
        const string original = """
        {
          "agreementId": "AGR-T-ORD",
          "parties": [{"id": "P1"}, {"id": "P2"}],
          "clauses": [
            {"id": "C1", "obligor": "P1", "amountFen": 500, "due": "2026-05-01"},
            {"id": "C2", "obligor": "P2", "amountFen": 300, "due": "2026-04-15", "references": ["C1", "C404"]}
          ],
          "amendments": [
            {"id": "A1", "replaces": "C1", "newClauseId": "C1-R1", "amountFen": 600},
            {"id": "A2", "replaces": "C404", "newClauseId": "CX"}
          ],
          "signatures": [
            {"partyId": "P1", "scope": ["AGR-T-ORD", "A1"]},
            {"partyId": "P2", "scope": ["AGR-T-ORD"]}
          ],
          "totalAmountFen": 800
        }
        """;

        // 每个对象的键全部倒序重写，数组顺序保持不变。
        const string shuffled = """
        {
          "totalAmountFen": 800,
          "signatures": [
            {"scope": ["AGR-T-ORD", "A1"], "partyId": "P1"},
            {"scope": ["AGR-T-ORD"], "partyId": "P2"}
          ],
          "amendments": [
            {"amountFen": 600, "newClauseId": "C1-R1", "replaces": "C1", "id": "A1"},
            {"newClauseId": "CX", "replaces": "C404", "id": "A2"}
          ],
          "clauses": [
            {"due": "2026-05-01", "amountFen": 500, "obligor": "P1", "id": "C1"},
            {"references": ["C1", "C404"], "due": "2026-04-15", "amountFen": 300, "obligor": "P2", "id": "C2"}
          ],
          "parties": [{"id": "P1"}, {"id": "P2"}],
          "agreementId": "AGR-T-ORD"
        }
        """;

        Assert.Equal(AgreementAuditor.Audit(original).ToJson(), AgreementAuditor.Audit(shuffled).ToJson());
    }

    [Fact]
    public void RepeatedSubmission_ProducesIdenticalResult()
    {
        // 重复提交同一协议包（含重复提交完全相同的签署记录），结果必须一致且无放大。
        const string json = """
        {
          "agreementId": "AGR-T-DUP",
          "parties": [{"id": "P1"}],
          "clauses": [{"id": "C1", "obligor": "P1", "references": ["C404"]}],
          "signatures": [
            {"partyId": "P1", "scope": ["AGR-T-DUP", "AMD-X"]},
            {"partyId": "P1", "scope": ["AGR-T-DUP", "AMD-X"]}
          ]
        }
        """;

        AuditResult first = AgreementAuditor.Audit(json);
        AuditResult second = AgreementAuditor.Audit(json);

        Assert.Equal(first.ToJson(), second.ToJson());
        // 同一路径的同一问题不因重复记录而重复报告。
        Assert.Equal(
            first.Issues.Count,
            first.Issues.Select(i => (i.Code, i.EvidencePath)).Distinct().Count());
    }

    [Fact]
    public void DeepReferenceChain_HandledWithoutStackOverflow()
    {
        // 20 000 层深的引用链 C_i -> C_{i-1}，外加 2 000 层深的补充协议替换链。
        // 所有图算法均为迭代实现，必须正常完成且没有问题（签署范围覆盖全部文件）。
        const int chainLength = 20_000;
        const int amendmentChain = 2_000;
        var sb = new StringBuilder();
        sb.Append("{\"agreementId\":\"AGR-T-DEEP\",\"parties\":[{\"id\":\"P1\"}],\"clauses\":[");
        for (int i = 0; i < chainLength; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"id\":\"C").Append(i).Append("\",\"obligor\":\"P1\"");
            if (i > 0)
            {
                sb.Append(",\"references\":[\"C").Append(i - 1).Append("\"]");
            }

            sb.Append('}');
        }

        sb.Append("],\"amendments\":[");
        for (int i = 0; i < amendmentChain; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            string target = i == 0 ? "C0" : $"R{i - 1}";
            sb.Append("{\"id\":\"AM").Append(i).Append("\",\"replaces\":\"").Append(target)
                .Append("\",\"newClauseId\":\"R").Append(i).Append("\"}");
        }

        sb.Append("],\"signatures\":[{\"partyId\":\"P1\",\"scope\":[\"AGR-T-DEEP\"");
        for (int i = 0; i < amendmentChain; i++)
        {
            sb.Append(",\"AM").Append(i).Append('"');
        }

        sb.Append("]}]}");

        AuditResult result = AgreementAuditor.Audit(sb.ToString());

        // C0 被 2 000 层补充协议链最终替换为 R1999：C1 对 C0 的引用是唯一的陈旧引用，
        // 该断言同时验证替换链被完整迭代追踪到末端。
        AuditIssue issue = Assert.Single(result.Issues);
        Assert.Equal(IssueCodes.StaleReference, issue.Code);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Equal("$.clauses[1].references[0]", issue.EvidencePath);
        Assert.Contains("R1999", issue.Message);
    }

    [Fact]
    public void DeepReferenceChain_WithBackEdge_ReportsSingleCycle()
    {
        // 20 000 层深的链，末端回指起点：整条链构成一个有向环，必须恰好报告一次。
        const int chainLength = 20_000;
        var sb = new StringBuilder();
        sb.Append("{\"agreementId\":\"AGR-T-DEEPCYC\",\"parties\":[{\"id\":\"P1\"}],\"clauses\":[");
        for (int i = 0; i < chainLength; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            string target = i == 0 ? $"C{chainLength - 1}" : $"C{i - 1}";
            sb.Append("{\"id\":\"C").Append(i).Append("\",\"obligor\":\"P1\",\"references\":[\"")
                .Append(target).Append("\"]}");
        }

        sb.Append("],\"signatures\":[{\"partyId\":\"P1\",\"scope\":[\"AGR-T-DEEPCYC\"]}]}");

        AuditResult result = AgreementAuditor.Audit(sb.ToString());

        AuditIssue issue = Assert.Single(result.Issues);
        Assert.Equal(IssueCodes.ReferenceCycle, issue.Code);
        Assert.Equal("$.clauses[0].references[0]", issue.EvidencePath);
    }

    [Fact]
    public void MaliciouslyDeepJson_ThrowsControlledException()
    {
        // 2 000 层合法嵌套数组，超过解析器 1 024 层深度上限：必须抛出受控异常而非栈溢出。
        string json = new string('[', 2_000) + "1" + new string(']', 2_000);

        Assert.Throws<AuditInputException>(() => AgreementAuditor.Audit(json));
    }

    [Fact]
    public void RandomAgreementGraphs_DeterministicAndEvidenceResolvable()
    {
        // 固定种子的随机协议图：随机条款引用（含悬空）、随机补充协议（含撤回、冲突、
        // 落空目标）、随机签署范围。对每份样本断言：
        // 1) 两次审计结果逐字节一致；2) 问题代码均为 MED_ 前缀；
        // 3) 每条证据路径都能解析到原始 JSON 中的真实节点；4) 输出按问题代码稳定排序。
        for (int seed = 0; seed < 30; seed++)
        {
            string json = BuildRandomAgreementJson(seed);
            AuditResult first = AgreementAuditor.Audit(json);
            AuditResult second = AgreementAuditor.Audit(json);

            Assert.Equal(first.ToJson(), second.ToJson());
            Assert.Equal(
                first.Issues.Select(i => i.Code).OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                first.Issues.Select(i => i.Code).ToArray());

            using JsonDocument doc = JsonDocument.Parse(json);
            foreach (AuditIssue issue in first.Issues)
            {
                Assert.StartsWith("MED_", issue.Code, StringComparison.Ordinal);
                Assert.True(
                    EvidencePathResolves(doc.RootElement, issue.EvidencePath),
                    $"种子 {seed}：证据路径 {issue.EvidencePath} 无法在原始 JSON 中解析（{issue.Code}）");
            }
        }
    }

    private static string BuildRandomAgreementJson(int seed)
    {
        var random = new Random(seed);
        int clauseCount = random.Next(2, 40);
        int partyCount = random.Next(1, 4);
        int amendmentCount = random.Next(0, 12);

        var sb = new StringBuilder();
        sb.Append("{\"agreementId\":\"AGR-R").Append(seed).Append("\",\"parties\":[");
        for (int i = 0; i < partyCount; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"id\":\"P").Append(i).Append("\"}");
        }

        sb.Append("],\"clauses\":[");
        for (int i = 0; i < clauseCount; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"id\":\"C").Append(i).Append("\",\"obligor\":\"P")
                .Append(random.Next(partyCount + 1)).Append('"'); // 偶尔产生未知义务人 P<partyCount>
            if (random.Next(2) == 0)
            {
                sb.Append(",\"amountFen\":").Append(random.Next(1, 1000));
            }

            if (random.Next(2) == 0)
            {
                sb.Append(",\"due\":\"2026-").Append(random.Next(1, 13).ToString("D2")).Append("-")
                    .Append(random.Next(1, 29).ToString("D2")).Append('"');
            }

            int refCount = random.Next(0, 3);
            if (refCount > 0)
            {
                sb.Append(",\"references\":[");
                for (int r = 0; r < refCount; r++)
                {
                    if (r > 0)
                    {
                        sb.Append(',');
                    }

                    // 1/4 概率引用不存在的编号，产生悬空引用。
                    int target = random.Next(4) == 0 ? clauseCount + random.Next(3) : random.Next(clauseCount);
                    sb.Append("\"C").Append(target).Append('"');
                }

                sb.Append(']');
            }

            sb.Append('}');
        }

        sb.Append("],\"amendments\":[");
        var producedIds = new List<string>();
        for (int i = 0; i < amendmentCount; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            // 目标：基础条款、先前补充协议产物或落空编号，分别模拟替换链与落空/冲突。
            string target = random.Next(4) switch
            {
                0 when producedIds.Count > 0 => producedIds[random.Next(producedIds.Count)],
                1 => "C" + (clauseCount + random.Next(3)),
                _ => "C" + random.Next(clauseCount),
            };
            string newId = $"R{i}";
            producedIds.Add(newId);
            sb.Append("{\"id\":\"AM").Append(i).Append("\",\"replaces\":\"").Append(target)
                .Append("\",\"newClauseId\":\"").Append(newId).Append('"');
            if (random.Next(3) == 0)
            {
                sb.Append(",\"amountFen\":").Append(random.Next(1, 1000));
            }

            if (random.Next(3) == 0)
            {
                sb.Append(",\"due\":\"2026-").Append(random.Next(1, 13).ToString("D2")).Append("-")
                    .Append(random.Next(1, 29).ToString("D2")).Append('"');
            }

            if (random.Next(5) == 0)
            {
                sb.Append(",\"withdrawn\":true");
            }

            sb.Append('}');
        }

        sb.Append("],\"signatures\":[");
        for (int i = 0; i < partyCount; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"partyId\":\"P").Append(i).Append("\",\"scope\":[\"AGR-R").Append(seed).Append('"');
            for (int a = 0; a < amendmentCount; a++)
            {
                if (random.Next(3) > 0)
                {
                    sb.Append(",\"AM").Append(a).Append('"');
                }
            }

            if (random.Next(4) == 0)
            {
                sb.Append(",\"AM999\""); // 偶尔签署未知编号。
            }

            sb.Append("]}");
        }

        sb.Append(']');
        if (random.Next(2) == 0)
        {
            sb.Append(",\"totalAmountFen\":").Append(random.Next(1, 20000));
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static bool EvidencePathResolves(JsonElement root, string path)
    {
        JsonElement current = root;
        int i = 0;
        if (path.StartsWith("$", StringComparison.Ordinal))
        {
            i = 1;
        }

        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                int start = ++i;
                while (i < path.Length && path[i] != '.' && path[i] != '[')
                {
                    i++;
                }

                string name = path[start..i];
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    return false;
                }
            }
            else if (path[i] == '[')
            {
                int close = path.IndexOf(']', i);
                if (close < 0 || !int.TryParse(path.AsSpan(i + 1, close - i - 1), out int index))
                {
                    return false;
                }

                if (current.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                int k = 0;
                bool found = false;
                foreach (JsonElement item in current.EnumerateArray())
                {
                    if (k++ == index)
                    {
                        current = item;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }

                i = close + 1;
            }
            else
            {
                return false;
            }
        }

        return true;
    }
}
