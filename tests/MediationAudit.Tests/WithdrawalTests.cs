using MediationAudit;
using Xunit;

namespace MediationAudit.Tests;

/// <summary>
/// 签署范围与撤回测试：区分撤回补充协议、撤回整个协议、移除当事人、撤回已签署事实四种语义；
/// 同一时间戳下乱序到达的事件按素材规定的事件序号确定结果。
/// </summary>
public class WithdrawalTests
{
    [Fact]
    public void AmendmentWithdrawalEvent_VoidsAmendmentAndScope()
    {
        // 撤回某份补充协议：A1 经撤回事件作废（与静态 withdrawn 同效），不产生版本、不要求签署；
        // 已签署的 A1 范围条目变为非当前有效文件。
        const string json = """
        {
          "agreementId": "AGR-W-AMD",
          "parties": [{"id": "P1"}],
          "clauses": [{"id": "C1", "obligor": "P1", "amountFen": 100}],
          "amendments": [{"id": "A1", "replaces": "C1", "newClauseId": "R1", "amountFen": 200}],
          "signatures": [{"partyId": "P1", "scope": ["AGR-W-AMD", "A1"], "seq": 1}],
          "withdrawals": [{"seq": 2, "at": "2026-08-01T10:05:00Z", "target": "amendment", "amendmentId": "A1"}]
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        AuditIssue issue = Assert.Single(result.Issues);
        Assert.Equal(IssueCodes.SignatureScopeUnknown, issue.Code);
        Assert.Equal("$.signatures[0].scope[1]", issue.EvidencePath);
        Assert.Equal(new[] { "A1" }, result.Versions.WithdrawnAmendmentIds.ToArray());
        Assert.Equal("C1", Assert.Single(result.Versions.EffectiveVersions).Id);
    }

    [Fact]
    public void AgreementWithdrawal_EmptyEffectiveViewAndSkipsChecks()
    {
        // 撤回整个协议：全部版本进入已撤回状态，有效视图为空；
        // 悬空引用、金额、签署等检查一律不再适用，不产生任何问题。
        const string json = """
        {
          "agreementId": "AGR-W-ALL",
          "parties": [{"id": "P1"}],
          "clauses": [{"id": "C1", "obligor": "P1", "amountFen": 100, "references": ["C404"]}],
          "signatures": [{"partyId": "P1", "scope": ["AGR-W-ALL"]}],
          "totalAmountFen": 999,
          "withdrawals": [{"seq": 1, "target": "agreement"}]
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        Assert.Empty(result.Issues);
        Assert.True(result.Versions.AgreementWithdrawn);
        Assert.Empty(result.Versions.EffectiveVersions);
        Assert.All(result.Versions.Versions, v => Assert.Equal(VersionStatus.Withdrawn, v.Status));
        Assert.Contains("\"agreementWithdrawn\": true", result.ToJson());
    }

    [Fact]
    public void PartyRemoval_OrphansObligationsAndSignatures()
    {
        // 移除当事人：P2 退出协议——其义务人身份与签署按悬空引用处理，
        // 但不再要求 P2 签署（不产生签署范围缺口）。
        const string json = """
        {
          "agreementId": "AGR-W-PTY",
          "parties": [{"id": "P1"}, {"id": "P2"}],
          "clauses": [
            {"id": "C1", "obligor": "P2", "amountFen": 100},
            {"id": "C2", "obligor": "P1", "amountFen": 200}
          ],
          "signatures": [
            {"partyId": "P1", "scope": ["AGR-W-PTY"]},
            {"partyId": "P2", "scope": ["AGR-W-PTY"]}
          ],
          "withdrawals": [{"seq": 1, "target": "party", "partyId": "P2"}]
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        Assert.Equal(
            new[]
            {
                (IssueCodes.UnknownReference, "$.clauses[0].obligor"),
                (IssueCodes.UnknownReference, "$.signatures[1].partyId"),
            },
            result.Issues.Select(i => (i.Code, i.EvidencePath)).ToArray());
        Assert.Equal(new[] { "P2" }, result.Versions.RemovedPartyIds.ToArray());
    }

    [Fact]
    public void RescindedSignature_ReportedNotErased()
    {
        // 撤回已签署事实：必须报 MED_SIGNATURE_RESCINDED 而非把签署从历史中抹掉——
        // 签署记录仍在原始 JSON 中，撤回行为被单独报告，且覆盖随之出现缺口。
        const string json = """
        {
          "agreementId": "AGR-W-RES",
          "parties": [{"id": "P1"}],
          "clauses": [{"id": "C1", "obligor": "P1", "amountFen": 100}],
          "amendments": [{"id": "A1", "replaces": "C1", "newClauseId": "R1", "amountFen": 200}],
          "signatures": [{"partyId": "P1", "scope": ["AGR-W-RES", "A1"], "seq": 1}],
          "withdrawals": [{"seq": 2, "target": "signature", "partyId": "P1"}]
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        Assert.Equal(
            new[]
            {
                (IssueCodes.SignatureRescinded, "$.withdrawals[0].partyId"),
                (IssueCodes.SignatureScopeGap, "$.signatures[0].scope"),
                (IssueCodes.SignatureScopeGap, "$.signatures[0].scope"),
            },
            result.Issues.Select(i => (i.Code, i.EvidencePath)).ToArray());
        // 补充协议本身未受影响：R1 仍是有效条款链头。
        Assert.Equal("R1", Assert.Single(result.Versions.EffectiveVersions).Id);
        // 缺口同时指向协议本体与补充协议（两条缺口说明不同）。
        Assert.Equal(2, result.Issues.Count(i => i.Code == IssueCodes.SignatureScopeGap));
    }

    [Fact]
    public void OutOfOrderSameTimestampEvents_SeqDeterminesOutcome()
    {
        // 同一时间戳、乱序到达：数组里 seq=3 的签署排在 seq=1 之前，撤回事件 seq=2 最后到达。
        // 按事件序号重放：seq1 签署被 seq2 撤回，seq3 重新签署 [AGR-W-SEQ, A1] 生效——
        // 因此不存在签署范围缺口；若按到达顺序重放（撤回最后生效）会错误地报出两个缺口。
        const string json = """
        {
          "agreementId": "AGR-W-SEQ",
          "parties": [{"id": "P1"}],
          "clauses": [{"id": "C1", "obligor": "P1", "amountFen": 100}],
          "amendments": [{"id": "A1", "replaces": "C1", "newClauseId": "R1", "amountFen": 200}],
          "signatures": [
            {"partyId": "P1", "scope": ["AGR-W-SEQ", "A1"], "signedAt": "2026-08-01T10:00:00Z", "seq": 3},
            {"partyId": "P1", "scope": ["AGR-W-SEQ"], "signedAt": "2026-08-01T10:00:00Z", "seq": 1}
          ],
          "withdrawals": [
            {"seq": 2, "at": "2026-08-01T10:00:00Z", "target": "signature", "partyId": "P1"}
          ]
        }
        """;

        AuditResult result = AgreementAuditor.Audit(json);

        AuditIssue issue = Assert.Single(result.Issues);
        Assert.Equal(IssueCodes.SignatureRescinded, issue.Code);
        Assert.Equal("$.withdrawals[0].partyId", issue.EvidencePath);
        Assert.DoesNotContain(result.Issues, i => i.Code == IssueCodes.SignatureScopeGap);
    }

    [Fact]
    public void Determinism_DeepChainDuplicateInputAndFieldReorder_WithEvents()
    {
        // 5 000 层引用链 + 带序号事件（撤回签署、撤回补充协议）：
        // 重复审计与字段重排后的审计结论必须逐字节一致，且问题集合精确符合预期。
        const int chainLength = 5_000;
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"agreementId\":\"AGR-W-DEEP\",\"parties\":[{\"id\":\"P1\"}],\"clauses\":[");
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

        sb.Append("],\"amendments\":[{\"id\":\"A1\",\"replaces\":\"C0\",\"newClauseId\":\"R0\",\"amountFen\":1}]");
        sb.Append(",\"signatures\":[");
        sb.Append("{\"partyId\":\"P1\",\"scope\":[\"AGR-W-DEEP\"],\"seq\":1},");
        sb.Append("{\"partyId\":\"P1\",\"scope\":[\"AGR-W-DEEP\"],\"seq\":3}");
        sb.Append("],\"withdrawals\":[");
        sb.Append("{\"seq\":2,\"at\":\"2026-08-01T10:00:00Z\",\"target\":\"signature\",\"partyId\":\"P1\"},");
        sb.Append("{\"seq\":4,\"at\":\"2026-08-01T10:00:00Z\",\"target\":\"amendment\",\"amendmentId\":\"A1\"}");
        sb.Append("]}");
        string json = sb.ToString();

        AuditResult first = AgreementAuditor.Audit(json);
        AuditResult second = AgreementAuditor.Audit(json);
        AuditResult reordered = AgreementAuditor.Audit(ReverseJsonKeys(json));

        Assert.Equal(first.ToJson(), second.ToJson());
        Assert.Equal(first.ToJson(), reordered.ToJson());

        // 预期：seq1 签署被 seq2 撤回、seq3 重新签署覆盖协议本体；A1 被 seq4 撤回。
        // 唯一问题是被撤回的已签署事实；C0 保持有效（A1 作废），无签署缺口。
        AuditIssue issue = Assert.Single(first.Issues);
        Assert.Equal(IssueCodes.SignatureRescinded, issue.Code);
        Assert.Equal("$.withdrawals[0].partyId", issue.EvidencePath);
        Assert.Equal(new[] { "A1" }, first.Versions.WithdrawnAmendmentIds.ToArray());
        Assert.Equal(chainLength, first.Versions.EffectiveVersions.Count);
    }

    private static string ReverseJsonKeys(string json)
    {
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        var sb = new System.Text.StringBuilder();
        Write(doc.RootElement, sb);
        return sb.ToString();

        static void Write(System.Text.Json.JsonElement el, System.Text.StringBuilder sb)
        {
            switch (el.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    sb.Append('{');
                    System.Text.Json.JsonProperty[] props = el.EnumerateObject().Reverse().ToArray();
                    for (int i = 0; i < props.Length; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(',');
                        }

                        sb.Append(System.Text.Json.JsonSerializer.Serialize(props[i].Name)).Append(':');
                        Write(props[i].Value, sb);
                    }

                    sb.Append('}');
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    sb.Append('[');
                    int k = 0;
                    foreach (System.Text.Json.JsonElement item in el.EnumerateArray())
                    {
                        if (k++ > 0)
                        {
                            sb.Append(',');
                        }

                        Write(item, sb);
                    }

                    sb.Append(']');
                    break;
                default:
                    sb.Append(el.GetRawText());
                    break;
            }
        }
    }
}
