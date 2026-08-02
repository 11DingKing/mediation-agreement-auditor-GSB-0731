using MediationAudit;
using Xunit;

namespace MediationAudit.Tests;

/// <summary>
/// 有向版本链测试：替换链、追加、并发分叉（含交换补充协议数组顺序）、替换环与编号汇聚，
/// 验证版本保留、有效视图、冲突问题码以及最短且稳定的证据路径。
/// </summary>
public class VersionChainTests
{
    [Fact]
    public void NoPhysicalDeletion_AllVersionsRetainedInChain()
    {
        // 干净的三次替换链：C1 -> R1 -> R2。旧版本必须全部保留在版本链中。
        const string json = """
        {
          "agreementId": "AGR-V-KEEP",
          "parties": [{"id": "P1"}],
          "clauses": [{"id": "C1", "obligor": "P1", "amountFen": 100, "due": "2026-01-01"}],
          "amendments": [
            {"id": "A1", "replaces": "C1", "newClauseId": "R1", "amountFen": 200},
            {"id": "A2", "replaces": "R1", "newClauseId": "R2", "amountFen": 300, "due": "2026-03-01"}
          ],
          "signatures": [{"partyId": "P1", "scope": ["AGR-V-KEEP", "A1", "A2"]}]
        }
        """;

        VersionChain chain = AgreementAuditor.BuildVersionChain(Agreement.Parse(json));

        Assert.Equal(
            new[]
            {
                ("C1", VersionStatus.Superseded),
                ("R1", VersionStatus.Superseded),
                ("R2", VersionStatus.Effective),
            },
            chain.Versions.Select(v => (v.Id, v.Status)).ToArray());
        Assert.Equal(
            new[] { ("C1", "R1", "A1", true), ("R1", "R2", "A2", true) },
            chain.Revisions.Select(r => (r.FromId, r.ToId, r.AmendmentId, r.Applied)).ToArray());

        // 有效视图：唯一的链头 R2，继承与覆写值按链解析。
        ClauseVersion head = Assert.Single(chain.EffectiveVersions);
        Assert.Equal("R2", head.Id);
        Assert.Equal(300, head.AmountFen);
        Assert.Equal(new DateOnly(2026, 3, 1), head.Due);
        Assert.Equal("$.amendments[1]", head.OriginPath);
    }

    [Fact]
    public void Append_NewClauseBecomesEffectiveAndAuditable()
    {
        // 追加夹具：A1 追加 C2（引用 C1）。追加条款进入有效视图并计入总额，A1 要求被签署。
        const string json = """
        {
          "agreementId": "AGR-V-APP",
          "parties": [{"id": "P1"}, {"id": "P2"}],
          "clauses": [{"id": "C1", "obligor": "P1", "amountFen": 100, "due": "2026-01-01"}],
          "amendments": [
            {"id": "A1", "appends": {"id": "C2", "obligor": "P2", "amountFen": 200, "due": "2026-02-01", "references": ["C1"]}}
          ],
          "signatures": [
            {"partyId": "P1", "scope": ["AGR-V-APP", "A1"]},
            {"partyId": "P2", "scope": ["AGR-V-APP", "A1"]}
          ],
          "totalAmountFen": 300
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        Assert.Empty(result.Issues);
        Assert.Equal(
            new[] { ("C1", "$.clauses[0]"), ("C2", "$.amendments[0].appends") },
            result.Versions.EffectiveVersions.Select(v => (v.Id, v.OriginPath)).ToArray());
        Assert.Empty(result.Versions.Revisions); // 追加不是修订边，是新的版本根。
    }

    [Fact]
    public void Append_WithDanglingReference_FlaggedAtAppendsPath()
    {
        // 追加条款内部的悬空引用：证据必须指向 appends 对象内的引用元素。
        const string json = """
        {
          "agreementId": "AGR-V-APPREF",
          "parties": [{"id": "P1"}],
          "clauses": [{"id": "C1", "obligor": "P1", "amountFen": 100}],
          "amendments": [
            {"id": "A1", "appends": {"id": "C2", "obligor": "P1", "references": ["C404"]}}
          ],
          "signatures": [{"partyId": "P1", "scope": ["AGR-V-APPREF", "A1"]}]
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        AuditIssue issue = Assert.Single(result.Issues);
        Assert.Equal(IssueCodes.UnknownReference, issue.Code);
        Assert.Equal("$.amendments[0].appends.references[0]", issue.EvidencePath);
    }

    [Fact]
    public void Append_IdCollision_FlaggedAsConflict()
    {
        // 追加编号撞上已有基础条款：编号归属不明，两个版本均未决。
        const string json = """
        {
          "agreementId": "AGR-V-APPDUP",
          "parties": [{"id": "P1"}],
          "clauses": [{"id": "C1", "obligor": "P1", "amountFen": 100}],
          "amendments": [
            {"id": "A1", "appends": {"id": "C1", "obligor": "P1", "amountFen": 200}}
          ],
          "signatures": [{"partyId": "P1", "scope": ["AGR-V-APPDUP"]}]
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        AuditIssue issue = Assert.Single(result.Issues);
        Assert.Equal(IssueCodes.AmendmentConflict, issue.Code);
        Assert.Equal("$.amendments[0].appends.id", issue.EvidencePath);
        Assert.All(result.Versions.Versions, v => Assert.Equal(VersionStatus.Contested, v.Status));
    }

    [Fact]
    public void Fork_SwappingAmendmentOrder_PreservesViewCodesAndEvidenceOrder()
    {
        // 分叉夹具及其补充协议逆序版本：有效条款视图、冲突问题码序列与 evidence 排序必须一致
        // （证据路径中的数组下标跟随输入位置，归一化后逐字节一致）。
        const string original = """
        {
          "agreementId": "AGR-V-FORK",
          "parties": [{"id": "P1"}],
          "clauses": [
            {"id": "C1", "obligor": "P1", "amountFen": 100, "due": "2026-01-01"},
            {"id": "C2", "obligor": "P1", "due": "2026-02-01", "references": ["C1", "C999"]}
          ],
          "amendments": [
            {"id": "A1", "replaces": "C1", "newClauseId": "R1", "amountFen": 200},
            {"id": "A2", "replaces": "C1", "newClauseId": "R2", "amountFen": 300},
            {"id": "A3", "replaces": "C404", "newClauseId": "RX"}
          ],
          "signatures": [{"partyId": "P1", "scope": ["AGR-V-FORK"]}]
        }
        """;

        const string swapped = """
        {
          "agreementId": "AGR-V-FORK",
          "parties": [{"id": "P1"}],
          "clauses": [
            {"id": "C1", "obligor": "P1", "amountFen": 100, "due": "2026-01-01"},
            {"id": "C2", "obligor": "P1", "due": "2026-02-01", "references": ["C1", "C999"]}
          ],
          "amendments": [
            {"id": "A3", "replaces": "C404", "newClauseId": "RX"},
            {"id": "A2", "replaces": "C1", "newClauseId": "R2", "amountFen": 300},
            {"id": "A1", "replaces": "C1", "newClauseId": "R1", "amountFen": 200}
          ],
          "signatures": [{"partyId": "P1", "scope": ["AGR-V-FORK"]}]
        }
        """;

        AuditResult a = AgreementAuditor.Audit(original);
        AuditResult b = AgreementAuditor.Audit(swapped);

        // 冲突问题码序列一致。
        Assert.Equal(a.Issues.Select(i => i.Code).ToArray(), b.Issues.Select(i => i.Code).ToArray());
        // 说明文案一致（与数组顺序无关）。
        Assert.Equal(a.Issues.Select(i => i.Message).ToArray(), b.Issues.Select(i => i.Message).ToArray());
        // evidence 排序一致：归一化补充协议下标后路径序列逐字节一致。
        Assert.Equal(
            a.Issues.Select(i => Normalize(i.EvidencePath)).ToArray(),
            b.Issues.Select(i => Normalize(i.EvidencePath)).ToArray());
        // 有效条款视图一致（分叉源 C1 保持有效，分支未决）。
        Assert.Equal(
            a.Versions.EffectiveVersions.Select(v => (v.Id, v.AmountFen, v.Due)).OrderBy(x => x.Id).ToArray(),
            b.Versions.EffectiveVersions.Select(v => (v.Id, v.AmountFen, v.Due)).OrderBy(x => x.Id).ToArray());
        Assert.Equal(
            new[] { "C1", "C2" },
            a.Versions.EffectiveVersions.Select(v => v.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // 原始输入下的精确断言：分叉证据取最短且稳定的一条（下标最小的并发替换）。
        Assert.Equal(
            new[]
            {
                (IssueCodes.AmendmentConflict, "$.amendments[0].replaces"),
                (IssueCodes.AmendmentTargetUnknown, "$.amendments[2].replaces"),
                (IssueCodes.UnknownReference, "$.clauses[1].references[1]"),
            },
            a.Issues.Select(i => (i.Code, i.EvidencePath)).ToArray());
    }

    [Fact]
    public void ReplacementCycle_ShortestStableEvidence()
    {
        // 替换环夹具：A1 把 C1 替换为 R1，A2 又把 R1 替换回 C1——环上版本全部未决，
        // 证据取环内 replaces 路径中最短且稳定的一条。
        const string json = """
        {
          "agreementId": "AGR-V-CYC",
          "parties": [{"id": "P1"}],
          "clauses": [{"id": "C1", "obligor": "P1", "amountFen": 100}],
          "amendments": [
            {"id": "A1", "replaces": "C1", "newClauseId": "R1"},
            {"id": "A2", "replaces": "R1", "newClauseId": "C1", "amountFen": 200}
          ],
          "signatures": [{"partyId": "P1", "scope": ["AGR-V-CYC"]}]
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        Assert.Equal(
            new[]
            {
                (IssueCodes.AmendmentConflict, "$.amendments[1].newClauseId"),
                (IssueCodes.ReferenceCycle, "$.amendments[0].replaces"),
            },
            result.Issues.Select(i => (i.Code, i.EvidencePath)).ToArray());
        Assert.All(result.Versions.Versions, v => Assert.Equal(VersionStatus.Contested, v.Status));
        Assert.Empty(result.Versions.EffectiveVersions);
    }

    [Fact]
    public void Convergence_ProductIdCollision_FlaggedWithShortestEvidence()
    {
        // 编号汇聚夹具：A1、A2 的产物都叫 R1。两个产物均未决，源条款各自保持有效。
        const string json = """
        {
          "agreementId": "AGR-V-CONV",
          "parties": [{"id": "P1"}],
          "clauses": [
            {"id": "C1", "obligor": "P1", "amountFen": 100},
            {"id": "C2", "obligor": "P1", "amountFen": 200}
          ],
          "amendments": [
            {"id": "A1", "replaces": "C1", "newClauseId": "R1", "amountFen": 150},
            {"id": "A2", "replaces": "C2", "newClauseId": "R1", "amountFen": 250}
          ],
          "signatures": [{"partyId": "P1", "scope": ["AGR-V-CONV"]}]
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        AuditIssue issue = Assert.Single(result.Issues);
        Assert.Equal(IssueCodes.AmendmentConflict, issue.Code);
        Assert.Equal("$.amendments[0].newClauseId", issue.EvidencePath);
        Assert.Equal(
            new[] { "C1", "C2" },
            result.Versions.EffectiveVersions.Select(v => v.Id).ToArray());
        Assert.Equal(2, result.Versions.Versions.Count(v => v.Id == "R1" && v.Status == VersionStatus.Contested));
    }

    private static string Normalize(string path)
        => System.Text.RegularExpressions.Regex.Replace(path, @"amendments\[\d+\]", "amendments[#]");
}
