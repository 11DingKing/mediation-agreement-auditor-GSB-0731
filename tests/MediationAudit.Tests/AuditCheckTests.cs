using MediationAudit;
using Xunit;

namespace MediationAudit.Tests;

/// <summary>
/// 针对每个 MED_ 问题代码的夹具级测试：一种代码对应至少一个最小夹具，
/// 断言代码、严重度与指向原始 JSON 的证据路径全部精确匹配。
/// </summary>
public class AuditCheckTests
{
    [Fact]
    public void BaseFixture_ProducesExpectedStableIssues()
    {
        // 仓库自带的材料夹具：补充协议替换 + 签署缺口的真实样本。
        string path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "materials", "agreements.json");
        string json = File.ReadAllText(path);

        AuditResult result = AgreementAuditor.Audit(json);

        Assert.Equal("AGR-2026-018", result.AgreementId);
        Assert.Equal(
            new[]
            {
                (IssueCodes.AmendmentTargetUnknown, "$.amendments[1].replaces"),
                (IssueCodes.DateOrder, "$.clauses[1].due"),
                (IssueCodes.SignatureScopeGap, "$.signatures[1].scope"),
                (IssueCodes.StaleReference, "$.clauses[1].references[0]"),
            },
            result.Issues.Select(i => (i.Code, i.EvidencePath)).ToArray());
        Assert.Equal(
            new[] { IssueSeverity.Error, IssueSeverity.Error, IssueSeverity.Error, IssueSeverity.Warning },
            result.Issues.Select(i => i.Severity).ToArray());
    }

    [Fact]
    public void UnknownReference_FlagsDanglingClauseObligorAndSigner()
    {
        // 悬空引用夹具：条款引用不存在的条款、义务人不是当事人、签署人不是当事人、签署范围含未知编号。
        const string json = """
        {
          "agreementId": "AGR-T-REF",
          "parties": [{"id": "P1", "name": "甲"}],
          "clauses": [
            {"id": "C1", "obligor": "P9", "amountFen": 100, "due": "2026-01-02"},
            {"id": "C2", "obligor": "P1", "references": ["C404"], "due": "2026-02-01"}
          ],
          "signatures": [
            {"partyId": "P1", "scope": ["AGR-T-REF", "AMD-X"]},
            {"partyId": "P9", "scope": ["AGR-T-REF"]}
          ]
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        Assert.Equal(
            new[]
            {
                (IssueCodes.SignatureScopeUnknown, "$.signatures[0].scope[1]"),
                (IssueCodes.UnknownReference, "$.clauses[0].obligor"),
                (IssueCodes.UnknownReference, "$.clauses[1].references[0]"),
                (IssueCodes.UnknownReference, "$.signatures[1].partyId"),
            },
            result.Issues.Select(i => (i.Code, i.EvidencePath)).ToArray());
    }

    [Fact]
    public void AmendmentChain_ForkKeepsSourceEffectiveAndFlagsConflict()
    {
        // 多次替换 + 并发替换夹具：A1/A2 构成链 C1 -> C1-R1 -> C1-R2，A3 与 A1 并发替换 C1。
        // 分叉语义与数组顺序无关：分叉源 C1 保持有效，全部分支未决；总额按有效视图核对（100 ≠ 300）。
        const string json = """
        {
          "agreementId": "AGR-T-CHAIN",
          "parties": [{"id": "P1"}, {"id": "P2"}],
          "clauses": [{"id": "C1", "obligor": "P1", "amountFen": 100, "due": "2026-01-01"}],
          "amendments": [
            {"id": "A1", "replaces": "C1", "newClauseId": "C1-R1", "amountFen": 200, "due": "2026-02-01"},
            {"id": "A2", "replaces": "C1-R1", "newClauseId": "C1-R2", "amountFen": 300, "due": "2026-03-01"},
            {"id": "A3", "replaces": "C1", "newClauseId": "C1-X", "amountFen": 999}
          ],
          "signatures": [
            {"partyId": "P1", "scope": ["AGR-T-CHAIN"]},
            {"partyId": "P2", "scope": ["AGR-T-CHAIN"]}
          ],
          "totalAmountFen": 300
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        Assert.Equal(
            new[]
            {
                (IssueCodes.AmendmentConflict, "$.amendments[0].replaces"),
                (IssueCodes.AmountMismatch, "$.totalAmountFen"),
            },
            result.Issues.Select(i => (i.Code, i.EvidencePath)).ToArray());

        // 有向版本链：全部旧版本保留，分叉源保持有效，分支一并未决。
        Assert.Equal(
            new[]
            {
                ("C1", VersionStatus.Effective),
                ("C1-R1", VersionStatus.Contested),
                ("C1-R2", VersionStatus.Contested),
                ("C1-X", VersionStatus.Contested),
            },
            result.Versions.Versions.Select(v => (v.Id, v.Status)).ToArray());
        Assert.Equal("C1", Assert.Single(result.Versions.EffectiveVersions).Id);
    }

    [Fact]
    public void WithdrawnAmendment_VoidsEffectsAndScope()
    {
        // 补充协议撤回夹具：A1 被撤回，其产物 C1-R1 视为不存在，
        // A2 因此替换落空；签署范围中的 A1/A2 都不是当前有效文件。
        const string json = """
        {
          "agreementId": "AGR-T-WD",
          "parties": [{"id": "P1"}, {"id": "P2"}],
          "clauses": [{"id": "C1", "obligor": "P1", "amountFen": 100, "due": "2026-01-01"}],
          "amendments": [
            {"id": "A1", "replaces": "C1", "newClauseId": "C1-R1", "amountFen": 200, "withdrawn": true},
            {"id": "A2", "replaces": "C1-R1", "newClauseId": "C1-R2", "amountFen": 300}
          ],
          "signatures": [
            {"partyId": "P1", "scope": ["AGR-T-WD", "A1", "A2"]},
            {"partyId": "P2", "scope": ["AGR-T-WD", "A2"]}
          ]
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        Assert.Equal(
            new[]
            {
                (IssueCodes.AmendmentTargetUnknown, "$.amendments[1].replaces"),
                (IssueCodes.SignatureScopeUnknown, "$.signatures[0].scope[1]"),
                (IssueCodes.SignatureScopeUnknown, "$.signatures[0].scope[2]"),
                (IssueCodes.SignatureScopeUnknown, "$.signatures[1].scope[1]"),
            },
            result.Issues.Select(i => (i.Code, i.EvidencePath)).ToArray());
    }

    [Fact]
    public void AmountAndDateDependency_AuditedAgainstEffectiveClauses()
    {
        // 金额与日期依赖夹具：A1 把 C1 金额改为 600、日期继承原值；
        // C2 依赖 C1 且履行日期更早；生效金额合计 900 与声明总额 800 不符。
        const string json = """
        {
          "agreementId": "AGR-T-DEP",
          "parties": [{"id": "P1"}, {"id": "P2"}],
          "clauses": [
            {"id": "C1", "obligor": "P1", "amountFen": 500, "due": "2026-05-01"},
            {"id": "C2", "obligor": "P2", "amountFen": 300, "due": "2026-04-15", "references": ["C1"]}
          ],
          "amendments": [
            {"id": "A1", "replaces": "C1", "newClauseId": "C1-R1", "amountFen": 600}
          ],
          "signatures": [
            {"partyId": "P1", "scope": ["AGR-T-DEP", "A1"]},
            {"partyId": "P2", "scope": ["AGR-T-DEP", "A1"]}
          ],
          "totalAmountFen": 800
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        Assert.Equal(
            new[]
            {
                (IssueCodes.AmountMismatch, "$.totalAmountFen"),
                (IssueCodes.DateOrder, "$.clauses[1].due"),
                (IssueCodes.StaleReference, "$.clauses[1].references[0]"),
            },
            result.Issues.Select(i => (i.Code, i.EvidencePath)).ToArray());
    }

    [Fact]
    public void DirectedCycles_FlaggedPerStronglyConnectedComponent()
    {
        // 有向环夹具：C1->C2->C3->C1 构成一个环，C4 自引用构成另一个环。
        const string json = """
        {
          "agreementId": "AGR-T-CYC",
          "parties": [{"id": "P1"}],
          "clauses": [
            {"id": "C1", "obligor": "P1", "references": ["C2"]},
            {"id": "C2", "obligor": "P1", "references": ["C3"]},
            {"id": "C3", "obligor": "P1", "references": ["C1"]},
            {"id": "C4", "obligor": "P1", "references": ["C4"]}
          ],
          "signatures": [{"partyId": "P1", "scope": ["AGR-T-CYC"]}]
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        Assert.Equal(
            new[]
            {
                (IssueCodes.ReferenceCycle, "$.clauses[0].references[0]"),
                (IssueCodes.ReferenceCycle, "$.clauses[3].references[0]"),
            },
            result.Issues.Select(i => (i.Code, i.EvidencePath)).ToArray());
    }

    [Fact]
    public void CleanAgreement_ProducesNoIssues()
    {
        // 普通协议夹具：无任何问题时应返回空列表，不产生误报。
        const string json = """
        {
          "agreementId": "AGR-T-OK",
          "parties": [{"id": "P1"}, {"id": "P2"}],
          "clauses": [
            {"id": "C1", "obligor": "P1", "amountFen": 100, "due": "2026-01-01"},
            {"id": "C2", "obligor": "P2", "amountFen": 200, "due": "2026-02-01", "references": ["C1"]}
          ],
          "signatures": [
            {"partyId": "P1", "scope": ["AGR-T-OK"]},
            {"partyId": "P2", "scope": ["AGR-T-OK"]}
          ],
          "totalAmountFen": 300
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        Assert.Empty(result.Issues);
    }
}
