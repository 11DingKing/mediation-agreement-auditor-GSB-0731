using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MediationAudit;

/// <summary>
/// 稳定的问题代码常量。代码是审计契约的一部分，统一使用 <c>MED_</c> 前缀，
/// 只表示问题的类别，不随说明文案、语言或遍历顺序的变化而变化。
/// </summary>
public static class IssueCodes
{
    /// <summary>条款引用了不存在的条款、义务人或签署人引用了不存在的当事人（悬空引用）。</summary>
    public const string UnknownReference = "MED_UNKNOWN_REFERENCE";

    /// <summary>补充协议声明要替换的条款不存在，或该条款由已撤回/未生效的补充协议产生。</summary>
    public const string AmendmentTargetUnknown = "MED_AMENDMENT_TARGET_UNKNOWN";

    /// <summary>
    /// 修订关系冲突：两份生效补充协议并发替换同一条款（分叉）、多份补充协议的产物占用同一编号（汇聚），
    /// 或补充协议未提供新编号而试图就地修改。
    /// </summary>
    public const string AmendmentConflict = "MED_AMENDMENT_CONFLICT";

    /// <summary>协议声明的金额总额与全部生效条款金额之和不一致。</summary>
    public const string AmountMismatch = "MED_AMOUNT_MISMATCH";

    /// <summary>条款的履行日期早于其所依赖（引用）条款的生效履行日期。</summary>
    public const string DateOrder = "MED_DATE_ORDER";

    /// <summary>有向环：条款引用关系在生效图上构成环，或补充协议替换关系在版本链上构成环（替换环）。</summary>
    public const string ReferenceCycle = "MED_REFERENCE_CYCLE";

    /// <summary>当事人的签署范围缺少协议本体或某份已生效补充协议。</summary>
    public const string SignatureScopeGap = "MED_SIGNATURE_SCOPE_GAP";

    /// <summary>签署范围中出现了非当前有效文件（未知编号、已撤回或未生效的补充协议）。</summary>
    public const string SignatureScopeUnknown = "MED_SIGNATURE_SCOPE_UNKNOWN";

    /// <summary>引用仍指向已被生效补充协议替换的条款，应改指向替换后的条款。</summary>
    public const string StaleReference = "MED_STALE_REFERENCE";

    /// <summary>
    /// 已签署事实被撤回（rescind）。签署记录保留在档案中不被删除或改写——撤回行为本身
    /// 作为稳定问题报告，且被撤回的签署不再计入签署范围覆盖。
    /// </summary>
    public const string SignatureRescinded = "MED_SIGNATURE_RESCINDED";
}

/// <summary>
/// 问题严重度。<see cref="Error"/> 表示必须处理的不一致；<see cref="Warning"/> 表示应确认的隐患。
/// </summary>
public enum IssueSeverity
{
    /// <summary>提示性隐患，不阻断但应核实。</summary>
    Warning = 0,

    /// <summary>明确的一致性问题。</summary>
    Error = 1,
}

/// <summary>
/// 版本链中某个条款版本的状态。
/// </summary>
public enum VersionStatus
{
    /// <summary>当前有效版本，参与金额、日期、签署等全部一致性检查。</summary>
    Effective = 0,

    /// <summary>已被唯一后继版本替换的旧版本。版本被保留在版本链中，不会被物理删除。</summary>
    Superseded = 1,

    /// <summary>
    /// 未决版本：处于替换分叉、产物编号冲突或替换环中，生效性无法唯一确定，
    /// 不参与一致性检查，等待工作人员裁决。
    /// </summary>
    Contested = 2,

    /// <summary>整个协议被撤回后，全部版本进入已撤回状态，不再参与任何检查。</summary>
    Withdrawn = 3,
}

/// <summary>
/// 撤回事件的目标类别：区分"撤回某份补充协议"、"撤回整个协议"、"移除当事人"与"撤回已签署事实"四种语义。
/// </summary>
public enum WithdrawalTarget
{
    /// <summary>撤回某份补充协议：该补充协议不产生任何版本（与静态 withdrawn 标记同效）。</summary>
    Amendment = 0,

    /// <summary>撤回整个协议：全部条款版本失效，有效条款视图为空。</summary>
    Agreement = 1,

    /// <summary>移除当事人：该当事人退出协议，其义务与签署按悬空引用规则处理。</summary>
    Party = 2,

    /// <summary>撤回已签署事实：签署记录保留，但撤回行为被报告且签署不再计入覆盖。</summary>
    Signature = 3,
}

/// <summary>
/// 一条撤回事件。对应 JSON 中 <c>withdrawals</c> 数组的元素。
/// 事件可以乱序到达、时间戳相同：结果一律由事件序号 <see cref="Seq"/> 决定，与到达顺序和时间戳无关。
/// </summary>
public sealed class Withdrawal
{
    /// <summary>事件序号；缺省时视为 -1（先于所有显式编号事件，按到达顺序排列）。</summary>
    public long? Seq { get; init; }

    /// <summary>事件时间戳（ISO-8601），仅用于展示，不参与排序。</summary>
    public string? At { get; init; }

    /// <summary>撤回目标类别。</summary>
    public required WithdrawalTarget Target { get; init; }

    /// <summary>目标为 <see cref="WithdrawalTarget.Amendment"/> 时的补充协议编号。</summary>
    public string? AmendmentId { get; init; }

    /// <summary>目标为 <see cref="WithdrawalTarget.Party"/> 或 <see cref="WithdrawalTarget.Signature"/> 时的当事人编号。</summary>
    public string? PartyId { get; init; }
}

/// <summary>
/// 当事人。对应 JSON 中 <c>parties</c> 数组的元素。
/// </summary>
public sealed class Party
{
    /// <summary>当事人编号，在协议包内唯一。</summary>
    public required string Id { get; init; }

    /// <summary>当事人名称，仅用于展示，不参与审计判断。</summary>
    public string? Name { get; init; }
}

/// <summary>
/// 一项金钱/行为义务：义务人、金额（分）与履行日期。
/// </summary>
public sealed class Obligation
{
    /// <summary>义务人（当事人编号），可能为 <c>null</c> 表示未填写。</summary>
    public string? ObligorId { get; init; }

    /// <summary>金额（单位：分），可能为 <c>null</c> 表示未填写。</summary>
    public long? AmountFen { get; init; }

    /// <summary>履行日期（yyyy-MM-dd），可能为 <c>null</c> 表示未填写。</summary>
    public DateOnly? Due { get; init; }
}

/// <summary>
/// 协议条款。对应 JSON 中 <c>clauses</c> 数组的元素。
/// </summary>
public sealed class Clause
{
    /// <summary>条款编号，在协议包内唯一。</summary>
    public required string Id { get; init; }

    /// <summary>该条款承载的义务。</summary>
    public required Obligation Obligation { get; init; }

    /// <summary>该条款依赖（引用）的其他条款编号，按 JSON 中出现顺序排列。</summary>
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 补充协议追加的新条款。对应 JSON 中 <c>amendments[i].appends</c> 对象，字段形状与基础条款一致。
/// </summary>
public sealed class AppendedClause
{
    /// <summary>新条款编号，不得与任何已有版本编号重复。</summary>
    public required string Id { get; init; }

    /// <summary>义务人（当事人编号）。</summary>
    public string? ObligorId { get; init; }

    /// <summary>金额（分）。</summary>
    public long? AmountFen { get; init; }

    /// <summary>履行日期（yyyy-MM-dd）。</summary>
    public DateOnly? Due { get; init; }

    /// <summary>该条款依赖（引用）的其他条款编号。</summary>
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 补充协议。对应 JSON 中 <c>amendments</c> 数组的元素。
/// 两种形态：<see cref="Replaces"/> 非空表示替换（修订）既有条款；<see cref="Appends"/> 非空表示追加新条款。
/// </summary>
public sealed class Amendment
{
    /// <summary>补充协议编号，在协议包内唯一。</summary>
    public required string Id { get; init; }

    /// <summary>被替换的条款编号（可以是基础条款，也可以是先前补充协议产生的条款）；追加形态下为 <c>null</c>。</summary>
    public string? Replaces { get; init; }

    /// <summary>
    /// 替换后新条款的编号；必须是一个未占用的新编号。不允许就地修改：
    /// 缺省或与已有编号重复都会被判为 <see cref="IssueCodes.AmendmentConflict"/>。
    /// </summary>
    public string? NewClauseId { get; init; }

    /// <summary>替换后的金额（分）；为 <c>null</c> 时继承被替换条款的金额。</summary>
    public long? AmountFen { get; init; }

    /// <summary>替换后的履行日期；为 <c>null</c> 时继承被替换条款的履行日期。</summary>
    public DateOnly? Due { get; init; }

    /// <summary>追加的新条款；与 <see cref="Replaces"/> 互斥。</summary>
    public AppendedClause? Appends { get; init; }

    /// <summary>是否已撤回。已撤回的补充协议不产生任何版本，也不要求被签署。</summary>
    public bool Withdrawn { get; init; }
}

/// <summary>
/// 一方当事人的签署记录。对应 JSON 中 <c>signatures</c> 数组的元素。
/// </summary>
public sealed class Signature
{
    /// <summary>签署方（当事人编号）。</summary>
    public required string PartyId { get; init; }

    /// <summary>签署范围：协议编号与补充协议编号的列表。</summary>
    public IReadOnlyList<string> Scope { get; init; } = Array.Empty<string>();

    /// <summary>签署时间（ISO-8601），仅用于展示，不参与审计判断。</summary>
    public string? SignedAt { get; init; }

    /// <summary>
    /// 事件序号：与撤回事件统一排序。同一时间戳下乱序到达的签署与撤回，结果由序号决定；
    /// 缺省时视为 -1（先于所有显式编号事件）。
    /// </summary>
    public long? Seq { get; init; }
}

/// <summary>
/// 协议包：一份调解协议及其全部当事人、条款、补充协议与签署记录。
/// 对应输入 JSON 的根对象。
/// </summary>
public sealed class Agreement
{
    /// <summary>协议编号。</summary>
    public required string Id { get; init; }

    /// <summary>当事人列表。</summary>
    public IReadOnlyList<Party> Parties { get; init; } = Array.Empty<Party>();

    /// <summary>基础条款列表。</summary>
    public IReadOnlyList<Clause> Clauses { get; init; } = Array.Empty<Clause>();

    /// <summary>补充协议列表。审计语义与数组顺序无关：交换顺序不改变有效条款视图与问题输出。</summary>
    public IReadOnlyList<Amendment> Amendments { get; init; } = Array.Empty<Amendment>();

    /// <summary>签署记录列表。签署即签署事件，可携带事件序号 <see cref="Signature.Seq"/>。</summary>
    public IReadOnlyList<Signature> Signatures { get; init; } = Array.Empty<Signature>();

    /// <summary>撤回事件列表。乱序到达时按事件序号 <see cref="Withdrawal.Seq"/> 确定结果。</summary>
    public IReadOnlyList<Withdrawal> Withdrawals { get; init; } = Array.Empty<Withdrawal>();

    /// <summary>协议声明的金额总额（分）；为 <c>null</c> 时不做总额一致性检查。</summary>
    public long? TotalAmountFen { get; init; }

    /// <summary>
    /// 从 JSON 文本解析协议包。JSON 对象的字段顺序不影响解析结果；
    /// 嵌套深度超过 1024 层的输入会被拒绝并抛出 <see cref="AuditInputException"/>，
    /// 解析过程完全基于迭代式读取器，不会消耗调用栈。
    /// </summary>
    /// <param name="json">协议包 JSON 文本。</param>
    /// <returns>解析得到的 <see cref="Agreement"/>。</returns>
    /// <exception cref="AuditInputException">JSON 无法解析或根对象缺少 <c>agreementId</c>。</exception>
    public static Agreement Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 1024 });
        }
        catch (JsonException ex)
        {
            throw new AuditInputException($"输入不是合法的 JSON（或嵌套过深）：{ex.Message}");
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AuditInputException("协议包根节点必须是 JSON 对象。");
            }

            if (!TryGetProperty(root, "agreementId", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.String)
            {
                throw new AuditInputException("协议包缺少字符串字段 agreementId。");
            }

            return new Agreement
            {
                Id = idEl.GetString()!,
                Parties = ParseParties(root),
                Clauses = ParseClauses(root),
                Amendments = ParseAmendments(root),
                Signatures = ParseSignatures(root),
                Withdrawals = ParseWithdrawals(root),
                TotalAmountFen = TryGetProperty(root, "totalAmountFen", out JsonElement totalEl)
                    && totalEl.ValueKind == JsonValueKind.Number
                    && totalEl.TryGetInt64(out long total)
                        ? total
                        : null,
            };
        }
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        // 逐属性枚举，字段顺序不影响结果。
        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (prop.NameEquals(name))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement obj, string name)
        => TryGetProperty(obj, name, out JsonElement el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static long? GetInt64(JsonElement obj, string name)
        => TryGetProperty(obj, name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long v) ? v : null;

    private static DateOnly? GetDate(JsonElement obj, string name)
    {
        string? s = GetString(obj, name);
        return s is not null
            && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d)
                ? d
                : null;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement obj, string name)
    {
        if (!TryGetProperty(obj, name, out JsonElement el) || el.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (JsonElement item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString()!);
            }
        }

        return list;
    }

    private static IReadOnlyList<Party> ParseParties(JsonElement root)
    {
        var list = new List<Party>();
        if (TryGetProperty(root, "parties", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? id = GetString(el, "id");
                if (id is not null)
                {
                    list.Add(new Party { Id = id, Name = GetString(el, "name") });
                }
            }
        }

        return list;
    }

    private static IReadOnlyList<Clause> ParseClauses(JsonElement root)
    {
        var list = new List<Clause>();
        if (TryGetProperty(root, "clauses", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? id = GetString(el, "id");
                if (id is null)
                {
                    continue;
                }

                list.Add(new Clause
                {
                    Id = id,
                    Obligation = new Obligation
                    {
                        ObligorId = GetString(el, "obligor"),
                        AmountFen = GetInt64(el, "amountFen"),
                        Due = GetDate(el, "due"),
                    },
                    References = GetStringArray(el, "references"),
                });
            }
        }

        return list;
    }

    private static IReadOnlyList<Amendment> ParseAmendments(JsonElement root)
    {
        var list = new List<Amendment>();
        if (TryGetProperty(root, "amendments", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? id = GetString(el, "id");
                if (id is null)
                {
                    continue;
                }

                list.Add(new Amendment
                {
                    Id = id,
                    Replaces = GetString(el, "replaces"),
                    NewClauseId = GetString(el, "newClauseId"),
                    AmountFen = GetInt64(el, "amountFen"),
                    Due = GetDate(el, "due"),
                    Appends = ParseAppends(el),
                    Withdrawn = TryGetProperty(el, "withdrawn", out JsonElement w)
                        && (w.ValueKind == JsonValueKind.True || w.ValueKind == JsonValueKind.False)
                        && w.GetBoolean(),
                });
            }
        }

        return list;
    }

    private static AppendedClause? ParseAppends(JsonElement amendment)
    {
        if (!TryGetProperty(amendment, "appends", out JsonElement el) || el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? id = GetString(el, "id");
        if (id is null)
        {
            return null;
        }

        return new AppendedClause
        {
            Id = id,
            ObligorId = GetString(el, "obligor"),
            AmountFen = GetInt64(el, "amountFen"),
            Due = GetDate(el, "due"),
            References = GetStringArray(el, "references"),
        };
    }

    private static IReadOnlyList<Signature> ParseSignatures(JsonElement root)
    {
        var list = new List<Signature>();
        if (TryGetProperty(root, "signatures", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? partyId = GetString(el, "partyId");
                if (partyId is null)
                {
                    continue;
                }

                list.Add(new Signature
                {
                    PartyId = partyId,
                    Scope = GetStringArray(el, "scope"),
                    SignedAt = GetString(el, "signedAt"),
                    Seq = GetInt64(el, "seq"),
                });
            }
        }

        return list;
    }

    private static IReadOnlyList<Withdrawal> ParseWithdrawals(JsonElement root)
    {
        var list = new List<Withdrawal>();
        if (TryGetProperty(root, "withdrawals", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                WithdrawalTarget? target = GetString(el, "target") switch
                {
                    "amendment" => WithdrawalTarget.Amendment,
                    "agreement" => WithdrawalTarget.Agreement,
                    "party" => WithdrawalTarget.Party,
                    "signature" => WithdrawalTarget.Signature,
                    _ => null,
                };
                string? amendmentId = GetString(el, "amendmentId");
                string? partyId = GetString(el, "partyId");

                // 目标类别必需配套的编号：缺失则该事件无法识别，跳过。
                if (target is null
                    || (target == WithdrawalTarget.Amendment && amendmentId is null)
                    || (target is WithdrawalTarget.Party or WithdrawalTarget.Signature && partyId is null))
                {
                    continue;
                }

                list.Add(new Withdrawal
                {
                    Seq = GetInt64(el, "seq"),
                    At = GetString(el, "at"),
                    Target = target.Value,
                    AmendmentId = amendmentId,
                    PartyId = partyId,
                });
            }
        }

        return list;
    }
}

/// <summary>
/// 输入协议包无法解析时抛出的异常（含恶意嵌套过深的输入）。调用方应将其视为受控错误。
/// </summary>
public sealed class AuditInputException : Exception
{
    /// <summary>以指定说明创建异常。</summary>
    public AuditInputException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 一条审计发现。问题的身份由 <see cref="Code"/> 与 <see cref="EvidencePath"/> 决定，
/// 与 <see cref="Message"/> 的文案无关。
/// </summary>
public sealed class AuditIssue
{
    /// <summary>稳定问题代码，见 <see cref="IssueCodes"/>。</summary>
    public required string Code { get; init; }

    /// <summary>严重度。</summary>
    public required IssueSeverity Severity { get; init; }

    /// <summary>面向工作人员的人类可读说明。文案可调整，不影响问题代码与排序。</summary>
    public required string Message { get; init; }

    /// <summary>
    /// 指向原始输入 JSON 的证据路径，格式为 <c>$.clauses[0].due</c>、<c>$.amendments[1].replaces</c> 等，
    /// 数组下标一律对应原始 JSON 中的位置。该路径在原始 JSON 中必定可以解析到具体节点。
    /// 分叉与替换环等结构性问题取所有候选路径中最短（段数最少）、再按字典序最小的一条，保证稳定。
    /// </summary>
    public required string EvidencePath { get; init; }
}

/// <summary>
/// 版本链中的一个条款版本：基础条款、替换产物或追加产物。旧版本始终保留，不会被物理删除。
/// </summary>
public sealed class ClauseVersion
{
    /// <summary>条款编号（复用基础条款与补充协议声明的编号）。</summary>
    public required string Id { get; init; }

    /// <summary>该版本在原始 JSON 中的出处，如 <c>$.clauses[0]</c>、<c>$.amendments[2]</c>、<c>$.amendments[1].appends</c>。</summary>
    public required string OriginPath { get; init; }

    /// <summary>版本状态（有效/已被替换/未决）。</summary>
    public required VersionStatus Status { get; init; }

    /// <summary>解析继承后的义务人。</summary>
    public string? ObligorId { get; init; }

    /// <summary>解析继承后的金额（分）。</summary>
    public long? AmountFen { get; init; }

    /// <summary>解析继承后的履行日期。</summary>
    public DateOnly? Due { get; init; }

    /// <summary>解析继承后的引用目标编号列表。</summary>
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 版本链中的一条修订边：补充协议把 <see cref="FromId"/> 版本替换为 <see cref="ToId"/> 版本。
/// </summary>
public sealed class RevisionLink
{
    /// <summary>产生该修订的补充协议编号。</summary>
    public required string AmendmentId { get; init; }

    /// <summary>被替换版本的条款编号。</summary>
    public required string FromId { get; init; }

    /// <summary>替换产物版本的条款编号。</summary>
    public required string ToId { get; init; }

    /// <summary>该修订关系在原始 JSON 中的证据路径（<c>$.amendments[i].replaces</c>）。</summary>
    public required string EvidencePath { get; init; }

    /// <summary>该修订是否实际生效（分叉、替换环、编号冲突或目标未知时为 <c>false</c>）。</summary>
    public required bool Applied { get; init; }
}

/// <summary>
/// 可审计的有向版本链：全部条款版本（含已被替换与未决版本）与全部修订边。
/// 结构只依赖协议包内容，与补充协议数组顺序无关。
/// </summary>
public sealed class VersionChain
{
    /// <summary>全部条款版本，按文档顺序排列；旧版本保留，不物理删除。</summary>
    public required IReadOnlyList<ClauseVersion> Versions { get; init; }

    /// <summary>全部修订边（替换关系），按文档顺序排列。</summary>
    public required IReadOnlyList<RevisionLink> Revisions { get; init; }

    /// <summary>有效条款视图：状态为 <see cref="VersionStatus.Effective"/> 的版本子集，按文档顺序排列。</summary>
    public required IReadOnlyList<ClauseVersion> EffectiveVersions { get; init; }

    /// <summary>整个协议是否已被撤回。为 <c>true</c> 时全部版本为已撤回状态，有效视图为空。</summary>
    public required bool AgreementWithdrawn { get; init; }

    /// <summary>已被撤回的补充协议编号（静态标记与撤回事件合并），按补充协议数组顺序排列。</summary>
    public required IReadOnlyList<string> WithdrawnAmendmentIds { get; init; }

    /// <summary>已被移除的当事人编号，按事件序号与到达顺序排列。</summary>
    public required IReadOnlyList<string> RemovedPartyIds { get; init; }
}

/// <summary>
/// 审计结果：协议编号、按稳定顺序排列的全部问题，以及审计所用的有向版本链。
/// </summary>
public sealed class AuditResult
{
    /// <summary>被审计协议的编号。</summary>
    public required string AgreementId { get; init; }

    /// <summary>
    /// 问题列表。排序规则：先按问题代码字典序，再按证据路径逐段比较（数组下标按数值比较），
    /// 与说明文案、JSON 字段顺序及图遍历顺序无关；同一输入重复审计结果完全一致。
    /// </summary>
    public required IReadOnlyList<AuditIssue> Issues { get; init; }

    /// <summary>审计所依据的有向版本链，可供工作人员逐版本、逐修订边复核。</summary>
    public required VersionChain Versions { get; init; }

    /// <summary>将审计结果序列化为确定性的 JSON 文本（两空格缩进，问题按稳定顺序排列）。</summary>
    /// <returns>JSON 文本。</returns>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("agreementId", AgreementId);
            writer.WriteBoolean("agreementWithdrawn", Versions.AgreementWithdrawn);
            writer.WriteNumber("issueCount", Issues.Count);
            writer.WriteStartArray("issues");
            foreach (AuditIssue issue in Issues)
            {
                writer.WriteStartObject();
                writer.WriteString("code", issue.Code);
                writer.WriteString("severity", issue.Severity.ToString());
                writer.WriteString("message", issue.Message);
                writer.WriteString("evidencePath", issue.EvidencePath);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("withdrawnAmendments");
            foreach (string id in Versions.WithdrawnAmendmentIds)
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("removedParties");
            foreach (string id in Versions.RemovedPartyIds)
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("effectiveVersions");
            foreach (ClauseVersion v in Versions.EffectiveVersions)
            {
                writer.WriteStartObject();
                writer.WriteString("id", v.Id);
                writer.WriteString("originPath", v.OriginPath);
                if (v.ObligorId is not null)
                {
                    writer.WriteString("obligor", v.ObligorId);
                }

                if (v.AmountFen is not null)
                {
                    writer.WriteNumber("amountFen", v.AmountFen.Value);
                }

                if (v.Due is not null)
                {
                    writer.WriteString("due", v.Due.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }

                writer.WriteStartArray("references");
                foreach (string r in v.References)
                {
                    writer.WriteStringValue(r);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// 调解协议一致性审计器。修订关系被建模为有向版本链（DAG），替换、追加、分叉、汇聚与
/// 替换环都以声明式规则判定，语义与补充协议数组顺序无关；所有图遍历（引用链追踪、
/// 环检测）均以显式栈迭代实现，任意深度的引用链与随机协议图都不会导致栈溢出。
/// </summary>
public static class AgreementAuditor
{
    /// <summary>
    /// 审计一份协议包 JSON，等价于先 <see cref="Agreement.Parse"/> 再 <see cref="Audit(Agreement)"/>。
    /// </summary>
    /// <param name="json">协议包 JSON 文本。</param>
    /// <returns>审计结果。</returns>
    /// <exception cref="AuditInputException">JSON 无法解析或嵌套过深。</exception>
    public static AuditResult Audit(string json) => Audit(Agreement.Parse(json));

    /// <summary>
    /// 对协议包执行全部一致性检查：先构建有向版本链，再检查悬空引用、签署范围、金额一致性、
    /// 履行日期顺序与引用环。返回的问题按（代码, 证据路径）稳定排序并去重。
    /// </summary>
    /// <param name="agreement">解析后的协议包。</param>
    /// <returns>审计结果。</returns>
    public static AuditResult Audit(Agreement agreement)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        var engine = new Engine(agreement);
        return engine.Run();
    }

    /// <summary>
    /// 仅构建可审计的有向版本链，不执行一致性检查。结构性问题（分叉、替换环、编号冲突）
    /// 通过 <see cref="VersionStatus.Contested"/> 与 <see cref="RevisionLink.Applied"/> 反映。
    /// </summary>
    /// <param name="agreement">解析后的协议包。</param>
    /// <returns>有向版本链。</returns>
    public static VersionChain BuildVersionChain(Agreement agreement)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        var builder = new ChainBuilder(agreement, static (_, _, _, _) => { });
        return builder.Build().ToPublicChain();
    }

    /// <summary>一条引用边：引用方写下的原始目标编号，以及它在原始 JSON 中的位置。</summary>
    private readonly struct RefEdge
    {
        public RefEdge(string rawTarget, string path)
        {
            RawTarget = rawTarget;
            Path = path;
        }

        public string RawTarget { get; }

        public string Path { get; }
    }

    /// <summary>版本链内部节点：记录自身覆写值与出处，继承值沿父链解析。</summary>
    private sealed class VersionNode
    {
        public required string Id { get; init; }

        public required string OriginPath { get; init; }

        /// <summary>文档顺序：基础条款在前，其后按补充协议数组顺序排列。</summary>
        public required int Order { get; init; }

        public string? OwnObligorId { get; init; }

        public string? OwnObligorPath { get; init; }

        public long? OwnAmountFen { get; init; }

        public DateOnly? OwnDue { get; init; }

        public string? OwnDuePath { get; init; }

        /// <summary>自有引用（基础条款与追加条款）；替换产物为 <c>null</c>，沿父链继承。</summary>
        public List<RefEdge>? OwnRefs { get; init; }

        /// <summary>替换来源（修订边起点）；基础条款与追加条款为 <c>null</c>。</summary>
        public VersionNode? Parent { get; set; }

        /// <summary>产生该版本的补充协议编号；基础条款为 <c>null</c>。</summary>
        public string? ProducedBy { get; init; }

        /// <summary>该版本编号在原始 JSON 中的出处（如 <c>$.clauses[0].id</c>、<c>$.amendments[1].newClauseId</c>）。</summary>
        public required string IdPath { get; init; }

        /// <summary>是否追加产物（追加条款无修订边来源）。</summary>
        public bool IsAppend { get; init; }

        public List<RevisionEdge> Out { get; } = new();

        public bool Contested { get; set; }

        public VersionStatus Status { get; set; } = VersionStatus.Effective;
    }

    /// <summary>版本链内部修订边。</summary>
    private sealed class RevisionEdge
    {
        public required VersionNode Source { get; init; }

        public required VersionNode Target { get; init; }

        public required string AmendmentId { get; init; }

        /// <summary><c>$.amendments[i].replaces</c>。</summary>
        public required string ReplacesPath { get; init; }

        /// <summary><c>$.amendments[i].newClauseId</c>（产物编号出处）。</summary>
        public required string NewClauseIdPath { get; init; }

        public required int AmendmentIndex { get; init; }

        public bool Applied { get; set; }
    }

    /// <summary>
    /// 有向版本链构建器。分阶段声明式构建：先创建全部版本节点，再连接修订边，
    /// 最后统一判定分叉、替换环与编号汇聚——因此语义与补充协议数组顺序无关。
    /// </summary>
    private sealed class ChainBuilder
    {
        private readonly Agreement _agreement;
        private readonly Action<string, IssueSeverity, string, string> _emit;
        private readonly List<VersionNode> _nodes = new();
        private readonly Dictionary<string, VersionNode> _canonical = new(StringComparer.Ordinal);
        private readonly List<RevisionEdge> _edges = new();
        private readonly HashSet<string> _withdrawnAmendmentIds = new(StringComparer.Ordinal);
        private bool _agreementWithdrawn;

        public ChainBuilder(Agreement agreement, Action<string, IssueSeverity, string, string> emit)
        {
            _agreement = agreement;
            _emit = emit;
        }

        public BuiltChain Build()
        {
            CollectWithdrawals();
            CreateBaseNodes();
            CreateProductNodes();
            ConnectEdges();
            FlagConvergences();
            FlagForks();
            FlagCycles();
            PropagateContested();
            ComputeStatuses();
            return new BuiltChain(_agreement, _nodes, _canonical, _edges, _agreementWithdrawn, WithdrawnAmendmentIdsInOrder(), RemovedPartyIdsInOrder());
        }

        /// <summary>
        /// 汇总撤回语义：撤回某份补充协议（静态标记或撤回事件）→ 不产生版本；
        /// 撤回整个协议 → 全部版本失效；移除当事人 → 记录于版本链供检查使用。
        /// 三者均为终态事实，与事件序号和到达顺序无关。
        /// </summary>
        private void CollectWithdrawals()
        {
            foreach (Amendment a in _agreement.Amendments)
            {
                if (a.Withdrawn)
                {
                    _withdrawnAmendmentIds.Add(a.Id);
                }
            }

            foreach (Withdrawal w in _agreement.Withdrawals)
            {
                switch (w.Target)
                {
                    case WithdrawalTarget.Amendment:
                        _withdrawnAmendmentIds.Add(w.AmendmentId!);
                        break;
                    case WithdrawalTarget.Agreement:
                        _agreementWithdrawn = true;
                        break;
                }
            }
        }

        private IReadOnlyList<string> WithdrawnAmendmentIdsInOrder()
            => _agreement.Amendments.Where(a => _withdrawnAmendmentIds.Contains(a.Id)).Select(a => a.Id).ToList();

        private IReadOnlyList<string> RemovedPartyIdsInOrder()
            => _agreement.Withdrawals
                .Where(w => w.Target == WithdrawalTarget.Party)
                .OrderBy(w => w.Seq ?? -1)
                .Select(w => w.PartyId!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        private void AddNode(VersionNode node)
        {
            _nodes.Add(node);
            // 同一编号可能有多个版本（编号冲突时）；规范节点取文档顺序最先者，用于引用解析起点。
            if (!_canonical.ContainsKey(node.Id))
            {
                _canonical.Add(node.Id, node);
            }
        }

        private void CreateBaseNodes()
        {
            for (int i = 0; i < _agreement.Clauses.Count; i++)
            {
                Clause c = _agreement.Clauses[i];
                if (_canonical.ContainsKey(c.Id))
                {
                    continue; // 基础条款编号重复时以先出现者为准，行为确定。
                }

                var refs = new List<RefEdge>();
                for (int j = 0; j < c.References.Count; j++)
                {
                    refs.Add(new RefEdge(c.References[j], $"$.clauses[{i}].references[{j}]"));
                }

                AddNode(new VersionNode
                {
                    Id = c.Id,
                    OriginPath = $"$.clauses[{i}]",
                    Order = i,
                    OwnObligorId = c.Obligation.ObligorId,
                    OwnObligorPath = c.Obligation.ObligorId is null ? null : $"$.clauses[{i}].obligor",
                    OwnAmountFen = c.Obligation.AmountFen,
                    OwnDue = c.Obligation.Due,
                    OwnDuePath = c.Obligation.Due is null ? null : $"$.clauses[{i}].due",
                    OwnRefs = refs,
                    IdPath = $"$.clauses[{i}].id",
                });
            }
        }

        private void CreateProductNodes()
        {
            for (int i = 0; i < _agreement.Amendments.Count; i++)
            {
                Amendment a = _agreement.Amendments[i];
                if (_withdrawnAmendmentIds.Contains(a.Id))
                {
                    continue; // 已撤回：不产生任何版本，也不要求签署。
                }

                if (a.Replaces is not null)
                {
                    if (string.IsNullOrEmpty(a.NewClauseId))
                    {
                        _emit(
                            IssueCodes.AmendmentConflict,
                            IssueSeverity.Error,
                            $"补充协议 '{a.Id}' 未提供 newClauseId；不允许就地修改条款 '{a.Replaces}'。",
                            $"$.amendments[{i}].replaces");
                        continue;
                    }

                    // 编号冲突（汇聚/撞号）不阻止建点：版本保留，统一在 FlagConvergences 判定。
                    AddNode(new VersionNode
                    {
                        Id = a.NewClauseId!,
                        OriginPath = $"$.amendments[{i}]",
                        Order = _agreement.Clauses.Count + i,
                        OwnAmountFen = a.AmountFen,
                        OwnDue = a.Due,
                        OwnDuePath = a.Due is null ? null : $"$.amendments[{i}].due",
                        ProducedBy = a.Id,
                        IdPath = $"$.amendments[{i}].newClauseId",
                    });
                }

                if (a.Appends is not null)
                {
                    AppendedClause app = a.Appends;
                    var refs = new List<RefEdge>();
                    for (int j = 0; j < app.References.Count; j++)
                    {
                        refs.Add(new RefEdge(app.References[j], $"$.amendments[{i}].appends.references[{j}]"));
                    }

                    AddNode(new VersionNode
                    {
                        Id = app.Id,
                        OriginPath = $"$.amendments[{i}].appends",
                        Order = _agreement.Clauses.Count + i,
                        OwnObligorId = app.ObligorId,
                        OwnObligorPath = app.ObligorId is null ? null : $"$.amendments[{i}].appends.obligor",
                        OwnAmountFen = app.AmountFen,
                        OwnDue = app.Due,
                        OwnDuePath = app.Due is null ? null : $"$.amendments[{i}].appends.due",
                        OwnRefs = refs,
                        ProducedBy = a.Id,
                        IdPath = $"$.amendments[{i}].appends.id",
                        IsAppend = true,
                    });
                }
            }
        }

        private void ConnectEdges()
        {
            for (int i = 0; i < _agreement.Amendments.Count; i++)
            {
                Amendment a = _agreement.Amendments[i];
                if (_withdrawnAmendmentIds.Contains(a.Id) || a.Replaces is null || string.IsNullOrEmpty(a.NewClauseId))
                {
                    continue;
                }

                if (!_canonical.TryGetValue(a.Replaces, out VersionNode? source))
                {
                    _emit(
                        IssueCodes.AmendmentTargetUnknown,
                        IssueSeverity.Error,
                        $"补充协议 '{a.Id}' 要替换的条款 '{a.Replaces}' 不存在（或产生它的补充协议已撤回）。",
                        $"$.amendments[{i}].replaces");

                    // 替换落空的产物版本保留在链中但标记未决：不进入有效视图，也不物理删除。
                    _nodes.First(n => n.ProducedBy == a.Id && n.Order == _agreement.Clauses.Count + i).Contested = true;
                    continue;
                }

                // 产物节点一定存在（CreateProductNodes 已无条件创建，包括撞号节点）。
                VersionNode target = _nodes.First(n =>
                    n.ProducedBy == a.Id && n.Order == _agreement.Clauses.Count + i);
                var edge = new RevisionEdge
                {
                    Source = source,
                    Target = target,
                    AmendmentId = a.Id,
                    ReplacesPath = $"$.amendments[{i}].replaces",
                    NewClauseIdPath = $"$.amendments[{i}].newClauseId",
                    AmendmentIndex = i,
                };
                source.Out.Add(edge);
                target.Parent = source;
                _edges.Add(edge);
            }
        }

        /// <summary>编号汇聚：同一编号被多个版本占用（多份补充协议产物撞号，或产物撞上基础条款）。</summary>
        private void FlagConvergences()
        {
            foreach (IGrouping<string, VersionNode> group in _nodes.GroupBy(n => n.Id).Where(g => g.Count() > 1))
            {
                List<VersionNode> members = group.OrderBy(n => n.Order).ToList();
                foreach (VersionNode node in members)
                {
                    node.Contested = true;
                }

                // 证据：相关补充协议产物编号出处（newClauseId / appends.id）中最短、最小的一条。
                List<string> paths = members
                    .Where(n => n.ProducedBy is not null)
                    .Select(n => n.IdPath)
                    .ToList();
                if (paths.Count > 0)
                {
                    string ids = string.Join("、", members.Where(n => n.ProducedBy is not null).Select(n => $"'{n.ProducedBy}'").OrderBy(s => s, StringComparer.Ordinal));
                    _emit(
                        IssueCodes.AmendmentConflict,
                        IssueSeverity.Error,
                        $"条款编号 '{group.Key}' 被多份补充协议（{ids}）的产物占用，相关版本均为未决。",
                        MinEvidencePath(paths));
                }
            }
        }

        /// <summary>分叉：同一版本被两份及以上补充协议并发替换。分叉源保持有效，全部分支未决。</summary>
        private void FlagForks()
        {
            foreach (VersionNode node in _nodes)
            {
                if (node.Out.Count < 2)
                {
                    continue;
                }

                foreach (RevisionEdge edge in node.Out)
                {
                    edge.Target.Contested = true;
                }

                string ids = string.Join("、", node.Out.Select(e => $"'{e.AmendmentId}'").OrderBy(s => s, StringComparer.Ordinal));
                _emit(
                    IssueCodes.AmendmentConflict,
                    IssueSeverity.Error,
                    $"条款 '{node.Id}' 被补充协议 {ids} 并发替换，产生分叉；裁决前 '{node.Id}' 保持为有效版本。",
                    MinEvidencePath(node.Out.Select(e => e.ReplacesPath)));
            }
        }

        /// <summary>替换环：把修订边投影到条款编号图上（同编号版本视为同一节点）检测有向环。环上版本全部未决。</summary>
        private void FlagCycles()
        {
            // 编号按文档首次出现顺序排列，保证遍历顺序固定。
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (VersionNode node in _nodes)
            {
                if (seen.Add(node.Id))
                {
                    ids.Add(node.Id);
                }
            }

            var adjacency = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
            foreach (RevisionEdge edge in _edges)
            {
                adjacency[edge.Source.Id].Add(edge.Target.Id);
            }

            foreach (List<string> scc in FindStronglyConnectedComponents(ids, id => adjacency[id]))
            {
                var memberIds = new HashSet<string>(scc, StringComparer.Ordinal);
                foreach (VersionNode node in _nodes.Where(n => memberIds.Contains(n.Id)))
                {
                    node.Contested = true;
                }

                List<string> paths = _edges
                    .Where(e => memberIds.Contains(e.Source.Id) && memberIds.Contains(e.Target.Id))
                    .Select(e => e.ReplacesPath)
                    .ToList();
                if (paths.Count > 0)
                {
                    string memberList = string.Join("→", scc.OrderBy(s => s, StringComparer.Ordinal));
                    _emit(
                        IssueCodes.ReferenceCycle,
                        IssueSeverity.Error,
                        $"补充协议替换构成有向环，涉及版本：{memberList}。",
                        MinEvidencePath(paths));
                }
            }
        }

        /// <summary>未决污染：未决版本的后继一并未决。迭代式广度优先传播，不消耗调用栈。</summary>
        private void PropagateContested()
        {
            var queue = new Queue<VersionNode>(_nodes.Where(n => n.Contested));
            var seen = new HashSet<VersionNode>(queue);
            while (queue.Count > 0)
            {
                VersionNode node = queue.Dequeue();
                foreach (RevisionEdge edge in node.Out)
                {
                    if (seen.Add(edge.Target))
                    {
                        edge.Target.Contested = true;
                        queue.Enqueue(edge.Target);
                    }
                }
            }
        }

        private void ComputeStatuses()
        {
            foreach (VersionNode node in _nodes)
            {
                if (node.Contested)
                {
                    node.Status = VersionStatus.Contested;
                    continue;
                }

                // 唯一后继且后继有效：本版本被替换；否则保持有效（无后继，或后继未决时保留原版）。
                if (node.Out.Count == 1 && !node.Out[0].Target.Contested)
                {
                    node.Status = VersionStatus.Superseded;
                }
            }

            // 生效修订边：源头无分叉、产物未决标记为空。
            foreach (RevisionEdge edge in _edges)
            {
                edge.Applied = edge.Source.Out.Count == 1 && !edge.Target.Contested;
            }

            // 撤回整个协议：全部版本失效（终态），所有修订边不再生效。
            if (_agreementWithdrawn)
            {
                foreach (VersionNode node in _nodes)
                {
                    node.Status = VersionStatus.Withdrawn;
                }

                foreach (RevisionEdge edge in _edges)
                {
                    edge.Applied = false;
                }
            }
        }

        /// <summary>最短且稳定的证据路径：先比段数，再按路径比较器逐段比较。</summary>
        internal static string MinEvidencePath(IEnumerable<string> paths)
            => paths.OrderBy(EvidencePathComparer.SegmentCount)
                .ThenBy(p => p, EvidencePathComparer.Instance)
                .First();

        /// <summary>迭代式 Tarjan 强连通分量算法：显式栈代替递归，任意深度的图都不会栈溢出。</summary>
        internal static List<List<T>> FindStronglyConnectedComponents<T>(
            IReadOnlyList<T> nodes,
            Func<T, List<T>> children)
            where T : notnull
        {
            var index = new Dictionary<T, int>();
            var low = new Dictionary<T, int>();
            var onStack = new HashSet<T>();
            var stack = new List<T>();
            var result = new List<List<T>>();
            var inLargeScc = new HashSet<T>();
            int counter = 0;

            foreach (T start in nodes)
            {
                if (index.ContainsKey(start))
                {
                    continue;
                }

                var callStack = new Stack<(T Node, int NextChild)>();
                index[start] = low[start] = counter++;
                stack.Add(start);
                onStack.Add(start);
                callStack.Push((start, 0));

                while (callStack.Count > 0)
                {
                    (T v, int next) = callStack.Pop();
                    List<T> successors = children(v);
                    if (next < successors.Count)
                    {
                        callStack.Push((v, next + 1));
                        T w = successors[next];
                        if (!index.ContainsKey(w))
                        {
                            index[w] = low[w] = counter++;
                            stack.Add(w);
                            onStack.Add(w);
                            callStack.Push((w, 0));
                        }
                        else if (onStack.Contains(w))
                        {
                            low[v] = Math.Min(low[v], index[w]);
                        }
                    }
                    else
                    {
                        if (low[v] == index[v])
                        {
                            var scc = new List<T>();
                            T w;
                            do
                            {
                                w = stack[^1];
                                stack.RemoveAt(stack.Count - 1);
                                onStack.Remove(w);
                                scc.Add(w);
                            }
                            while (!EqualityComparer<T>.Default.Equals(w, v));

                            if (scc.Count > 1)
                            {
                                result.Add(scc);
                                foreach (T m in scc)
                                {
                                    inLargeScc.Add(m);
                                }
                            }
                        }

                        if (callStack.Count > 0)
                        {
                            T parent = callStack.Peek().Node;
                            low[parent] = Math.Min(low[parent], low[v]);
                        }
                    }
                }
            }

            // 自环：节点指向自身且不属于更大的环。
            foreach (T node in nodes)
            {
                if (!inLargeScc.Contains(node) && children(node).Contains(node))
                {
                    result.Add(new List<T> { node });
                }
            }

            return result;
        }
    }

    /// <summary>构建完成的版本链内部视图：供一致性检查与公开投影使用。</summary>
    private sealed class BuiltChain
    {
        private readonly Agreement _agreement;

        public BuiltChain(
            Agreement agreement,
            List<VersionNode> nodes,
            Dictionary<string, VersionNode> canonical,
            List<RevisionEdge> edges,
            bool agreementWithdrawn,
            IReadOnlyList<string> withdrawnAmendmentIds,
            IReadOnlyList<string> removedPartyIds)
        {
            _agreement = agreement;
            Nodes = nodes;
            Canonical = canonical;
            Edges = edges;
            AgreementWithdrawn = agreementWithdrawn;
            WithdrawnAmendmentIds = withdrawnAmendmentIds;
            RemovedPartyIds = removedPartyIds;
            Effective = Nodes.Where(n => n.Status == VersionStatus.Effective).OrderBy(n => n.Order).ToList();
        }

        public List<VersionNode> Nodes { get; }

        public Dictionary<string, VersionNode> Canonical { get; }

        public List<RevisionEdge> Edges { get; }

        public List<VersionNode> Effective { get; }

        public bool AgreementWithdrawn { get; }

        public IReadOnlyList<string> WithdrawnAmendmentIds { get; }

        public IReadOnlyList<string> RemovedPartyIds { get; }

        /// <summary>实际生效（已被应用）的补充协议编号集合。</summary>
        public IEnumerable<string> AppliedAmendmentIds => _agreement.Amendments
            .Where(a => !WithdrawnAmendmentIds.Contains(a.Id))
            .Select(a => a.Id)
            .Where(id => Edges.Any(e => e.AmendmentId == id && e.Applied)
                || Nodes.Any(n => n.ProducedBy == id && n.IsAppend && n.Status != VersionStatus.Contested));

        /// <summary>沿父链解析义务人（取值与其原始 JSON 出处）。迭代实现，环安全。</summary>
        public (string? Value, string? Path) ResolveObligor(VersionNode node)
        {
            var visited = new HashSet<VersionNode>();
            VersionNode? current = node;
            while (current is not null && visited.Add(current))
            {
                if (current.OwnObligorPath is not null || current.Parent is null)
                {
                    return (current.OwnObligorId, current.OwnObligorPath);
                }

                current = current.Parent;
            }

            return (null, null);
        }

        /// <summary>沿父链解析金额。迭代实现，环安全。</summary>
        public long? ResolveAmount(VersionNode node)
        {
            var visited = new HashSet<VersionNode>();
            VersionNode? current = node;
            while (current is not null && visited.Add(current))
            {
                if (current.OwnAmountFen is not null || current.Parent is null)
                {
                    return current.OwnAmountFen;
                }

                current = current.Parent;
            }

            return null;
        }

        /// <summary>沿父链解析履行日期（取值与其原始 JSON 出处）。迭代实现，环安全。</summary>
        public (DateOnly? Value, string? Path) ResolveDue(VersionNode node)
        {
            var visited = new HashSet<VersionNode>();
            VersionNode? current = node;
            while (current is not null && visited.Add(current))
            {
                if (current.OwnDuePath is not null || current.Parent is null)
                {
                    return (current.OwnDue, current.OwnDuePath);
                }

                current = current.Parent;
            }

            return (null, null);
        }

        /// <summary>沿父链解析引用列表（替换产物继承被替换版本的引用）。</summary>
        public List<RefEdge> ResolveRefs(VersionNode node)
        {
            var visited = new HashSet<VersionNode>();
            VersionNode? current = node;
            while (current is not null && visited.Add(current))
            {
                if (current.OwnRefs is not null)
                {
                    return current.OwnRefs;
                }

                current = current.Parent;
            }

            return new List<RefEdge>();
        }

        /// <summary>
        /// 把引用目标编号解析到版本链头：从该编号的规范版本出发，沿唯一生效后继迭代前进。
        /// 返回 null 表示编号不存在（悬空）；返回未决节点表示目标处于分叉/环中。
        /// </summary>
        public VersionNode? ResolveHead(string rawId)
        {
            if (!Canonical.TryGetValue(rawId, out VersionNode? node))
            {
                return null;
            }

            var visited = new HashSet<VersionNode>();
            while (visited.Add(node)
                && node.Status != VersionStatus.Contested
                && node.Out.Count == 1
                && !node.Out[0].Target.Contested)
            {
                node = node.Out[0].Target;
            }

            return node;
        }

        /// <summary>投影为公开的可审计版本链。</summary>
        public VersionChain ToPublicChain()
        {
            ClauseVersion Project(VersionNode n) => new()
            {
                Id = n.Id,
                OriginPath = n.OriginPath,
                Status = n.Status,
                ObligorId = ResolveObligor(n).Value,
                AmountFen = ResolveAmount(n),
                Due = ResolveDue(n).Value,
                References = ResolveRefs(n).Select(r => r.RawTarget).ToList(),
            };

            return new VersionChain
            {
                Versions = Nodes.OrderBy(n => n.Order).Select(Project).ToList(),
                Revisions = Edges.OrderBy(e => e.AmendmentIndex).Select(e => new RevisionLink
                {
                    AmendmentId = e.AmendmentId,
                    FromId = e.Source.Id,
                    ToId = e.Target.Id,
                    EvidencePath = e.ReplacesPath,
                    Applied = e.Applied,
                }).ToList(),
                EffectiveVersions = Effective.Select(Project).ToList(),
                AgreementWithdrawn = AgreementWithdrawn,
                WithdrawnAmendmentIds = WithdrawnAmendmentIds,
                RemovedPartyIds = RemovedPartyIds,
            };
        }
    }

    private sealed class Engine
    {
        private readonly Agreement _agreement;
        private readonly List<AuditIssue> _issues = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly HashSet<int> _rescindedSignatures = new();
        private BuiltChain _chain = null!;

        public Engine(Agreement agreement) => _agreement = agreement;

        public AuditResult Run()
        {
            _chain = new ChainBuilder(_agreement, Emit).Build();
            ReplaySignatureEvents();
            CheckReferences();
            CheckDateOrder();
            if (!_chain.AgreementWithdrawn)
            {
                CheckAmounts();
                CheckSignatures();
            }

            CheckReferenceCycles();

            List<AuditIssue> sorted = _issues
                .OrderBy(i => i.Code, StringComparer.Ordinal)
                .ThenBy(i => i.EvidencePath, EvidencePathComparer.Instance)
                .ThenBy(i => i.Message, StringComparer.Ordinal)
                .ToList();

            return new AuditResult
            {
                AgreementId = _agreement.Id,
                Issues = sorted,
                Versions = _chain.ToPublicChain(),
            };
        }

        /// <summary>
        /// 签署事件与撤回事件合并为统一事件流，按（事件序号, 到达顺序）重放：
        /// 同一时间戳下乱序到达的撤回与新增签署，结果一律由事件序号决定。
        /// 被撤回的签署保留在档案中（记录数组下标），不报缺、不删除，只报
        /// <see cref="IssueCodes.SignatureRescinded"/> 且不再计入签署范围覆盖。
        /// </summary>
        private void ReplaySignatureEvents()
        {
            var events = new List<(long Seq, int Arrival, bool IsWithdrawal, int Index)>();
            for (int i = 0; i < _agreement.Signatures.Count; i++)
            {
                events.Add((_agreement.Signatures[i].Seq ?? -1, i, false, i));
            }

            for (int j = 0; j < _agreement.Withdrawals.Count; j++)
            {
                events.Add((_agreement.Withdrawals[j].Seq ?? -1, _agreement.Signatures.Count + j, true, j));
            }

            var active = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach ((long _, int _, bool isWithdrawal, int index) in events.OrderBy(e => e.Seq).ThenBy(e => e.Arrival))
            {
                if (!isWithdrawal)
                {
                    string pid = _agreement.Signatures[index].PartyId;
                    if (!active.TryGetValue(pid, out List<int>? list))
                    {
                        active[pid] = list = new List<int>();
                    }

                    list.Add(index);
                    continue;
                }

                Withdrawal w = _agreement.Withdrawals[index];
                if (w.Target != WithdrawalTarget.Signature)
                {
                    continue;
                }

                if (active.TryGetValue(w.PartyId!, out List<int>? signed) && signed.Count > 0)
                {
                    foreach (int s in signed)
                    {
                        _rescindedSignatures.Add(s);
                    }

                    signed.Clear();
                    Emit(
                        IssueCodes.SignatureRescinded,
                        IssueSeverity.Error,
                        $"当事人 '{w.PartyId}' 的已签署事实被撤回；签署记录保留在档案中，但不再计入签署范围覆盖。",
                        $"$.withdrawals[{index}].partyId");
                }
                else
                {
                    Emit(
                        IssueCodes.SignatureRescinded,
                        IssueSeverity.Error,
                        $"撤回事件要求撤回当事人 '{w.PartyId}' 的已签署事实，但按事件序号重放后其没有生效中的签署。",
                        $"$.withdrawals[{index}].partyId");
                }
            }
        }

        private void Emit(string code, IssueSeverity severity, string message, string evidencePath)
        {
            // 同一问题（代码+路径+文案）只报告一次：重复提交的相同内容不会放大结果。
            if (_seen.Add(code + "|" + evidencePath + "|" + message))
            {
                _issues.Add(new AuditIssue { Code = code, Severity = severity, Message = message, EvidencePath = evidencePath });
            }
        }

        private void CheckReferences()
        {
            foreach (VersionNode node in _chain.Effective)
            {
                (string? obligor, string? obligorPath) = _chain.ResolveObligor(node);
                if (obligor is not null
                    && obligorPath is not null
                    && !EffectivePartyIds().Contains(obligor))
                {
                    Emit(
                        IssueCodes.UnknownReference,
                        IssueSeverity.Error,
                        $"条款 '{node.Id}' 的义务人 '{obligor}' 不在当事人列表中。",
                        obligorPath);
                }

                foreach (RefEdge edge in _chain.ResolveRefs(node))
                {
                    VersionNode? head = _chain.ResolveHead(edge.RawTarget);
                    if (head is null)
                    {
                        Emit(
                            IssueCodes.UnknownReference,
                            IssueSeverity.Error,
                            $"条款 '{node.Id}' 引用了不存在的条款 '{edge.RawTarget}'。",
                            edge.Path);
                    }
                    else if (head.Status == VersionStatus.Effective && head.Id != edge.RawTarget)
                    {
                        string via = _chain.Canonical[edge.RawTarget].Out.Count > 0
                            ? _chain.Canonical[edge.RawTarget].Out[0].AmendmentId
                            : string.Empty;
                        Emit(
                            IssueCodes.StaleReference,
                            IssueSeverity.Warning,
                            $"引用仍指向已被补充协议 '{via}' 替换的条款 '{edge.RawTarget}'，应改指向 '{head.Id}'。",
                            edge.Path);
                    }

                    // 目标未决：分叉/替换环已各自报告，此处不重复。
                }
            }
        }

        private void CheckDateOrder()
        {
            foreach (VersionNode node in _chain.Effective)
            {
                (DateOnly? due, string? duePath) = _chain.ResolveDue(node);
                if (due is null || duePath is null)
                {
                    continue;
                }

                foreach (RefEdge edge in _chain.ResolveRefs(node))
                {
                    VersionNode? head = _chain.ResolveHead(edge.RawTarget);
                    if (head is null || head.Status != VersionStatus.Effective)
                    {
                        continue;
                    }

                    (DateOnly? targetDue, _) = _chain.ResolveDue(head);
                    if (targetDue is not null && due.Value < targetDue.Value)
                    {
                        Emit(
                            IssueCodes.DateOrder,
                            IssueSeverity.Error,
                            $"条款 '{node.Id}' 的履行日期 {due.Value:yyyy-MM-dd} 早于其依赖条款 '{head.Id}' 的履行日期 {targetDue.Value:yyyy-MM-dd}。",
                            duePath);
                    }
                }
            }
        }

        private void CheckAmounts()
        {
            if (_agreement.TotalAmountFen is null)
            {
                return;
            }

            long sum = 0;
            foreach (VersionNode node in _chain.Effective)
            {
                sum += _chain.ResolveAmount(node) ?? 0;
            }

            if (sum != _agreement.TotalAmountFen.Value)
            {
                Emit(
                    IssueCodes.AmountMismatch,
                    IssueSeverity.Error,
                    $"全部生效条款金额合计 {sum} 分，与协议声明总额 {_agreement.TotalAmountFen.Value} 分不一致。",
                    "$.totalAmountFen");
            }
        }

        /// <summary>当前有效的当事人编号：协议当事人减去被撤回事件移除者。</summary>
        private HashSet<string> EffectivePartyIds()
        {
            var removed = new HashSet<string>(_chain.RemovedPartyIds, StringComparer.Ordinal);
            return new HashSet<string>(
                _agreement.Parties.Where(p => !removed.Contains(p.Id)).Select(p => p.Id),
                StringComparer.Ordinal);
        }

        private void CheckSignatures()
        {
            HashSet<string> partyIds = EffectivePartyIds();

            // 有效签署范围：协议本体 + 已生效（已应用且未未决）补充协议。
            var validScope = new HashSet<string>(StringComparer.Ordinal) { _agreement.Id };
            foreach (string id in _chain.AppliedAmendmentIds)
            {
                validScope.Add(id);
            }

            for (int i = 0; i < _agreement.Signatures.Count; i++)
            {
                Signature sig = _agreement.Signatures[i];
                if (!partyIds.Contains(sig.PartyId))
                {
                    Emit(
                        IssueCodes.UnknownReference,
                        IssueSeverity.Error,
                        $"签署人 '{sig.PartyId}' 不在当事人列表中。",
                        $"$.signatures[{i}].partyId");
                }

                for (int j = 0; j < sig.Scope.Count; j++)
                {
                    if (!validScope.Contains(sig.Scope[j]))
                    {
                        Emit(
                            IssueCodes.SignatureScopeUnknown,
                            IssueSeverity.Error,
                            $"签署范围条目 '{sig.Scope[j]}' 不是当前有效的协议或补充协议编号（可能为未知编号、已撤回或未生效）。",
                            $"$.signatures[{i}].scope[{j}]");
                    }
                }
            }

            var removedParties = new HashSet<string>(_chain.RemovedPartyIds, StringComparer.Ordinal);
            for (int i = 0; i < _agreement.Parties.Count; i++)
            {
                Party party = _agreement.Parties[i];
                if (removedParties.Contains(party.Id))
                {
                    continue; // 已移除的当事人不再要求签署。
                }

                var signed = new HashSet<string>(StringComparer.Ordinal);
                int firstEntry = -1;
                for (int s = 0; s < _agreement.Signatures.Count; s++)
                {
                    if (_agreement.Signatures[s].PartyId == party.Id)
                    {
                        if (firstEntry < 0)
                        {
                            firstEntry = s;
                        }

                        // 被撤回的签署保留档案，但不计入覆盖。
                        if (_rescindedSignatures.Contains(s))
                        {
                            continue;
                        }

                        foreach (string scope in _agreement.Signatures[s].Scope)
                        {
                            signed.Add(scope);
                        }
                    }
                }

                foreach (string required in validScope.OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (signed.Contains(required))
                    {
                        continue;
                    }

                    string path = firstEntry >= 0 ? $"$.signatures[{firstEntry}].scope" : $"$.parties[{i}].id";
                    string message = firstEntry >= 0
                        ? $"当事人 '{party.Id}' 的签署范围缺少 '{required}'。"
                        : $"当事人 '{party.Id}' 没有任何签署记录，缺少对 '{required}' 的签署。";
                    Emit(IssueCodes.SignatureScopeGap, IssueSeverity.Error, message, path);
                }
            }
        }

        /// <summary>条款引用环：在生效条款视图上检测有向环，证据取环内引用边中最短且最小的一条。</summary>
        private void CheckReferenceCycles()
        {
            var edges = new Dictionary<VersionNode, List<(VersionNode Target, string Path)>>();
            foreach (VersionNode node in _chain.Effective)
            {
                var list = new List<(VersionNode, string)>();
                foreach (RefEdge edge in _chain.ResolveRefs(node))
                {
                    VersionNode? head = _chain.ResolveHead(edge.RawTarget);
                    if (head is not null && head.Status == VersionStatus.Effective)
                    {
                        list.Add((head, edge.Path));
                    }
                }

                edges[node] = list;
            }

            foreach (List<VersionNode> scc in ChainBuilder.FindStronglyConnectedComponents(_chain.Effective, n => edges[n].Select(e => e.Target).ToList()))
            {
                var members = new HashSet<VersionNode>(scc);
                List<string> paths = _chain.Effective
                    .Where(members.Contains)
                    .SelectMany(n => edges[n].Where(e => members.Contains(e.Target)).Select(e => e.Path))
                    .ToList();
                if (paths.Count > 0)
                {
                    string memberList = string.Join("、", _chain.Effective.Where(members.Contains).Select(n => n.Id));
                    Emit(
                        IssueCodes.ReferenceCycle,
                        IssueSeverity.Error,
                        $"条款引用构成有向环，涉及：{memberList}。",
                        ChainBuilder.MinEvidencePath(paths));
                }
            }
        }
    }

    /// <summary>
    /// 证据路径比较器：把 <c>$.clauses[10].references[2]</c> 拆成属性段与下标段逐段比较，
    /// 数组下标按数值而非字符串比较，保证 <c>clauses[2]</c> 排在 <c>clauses[10]</c> 之前。
    /// </summary>
    private sealed class EvidencePathComparer : IComparer<string>
    {
        public static readonly EvidencePathComparer Instance = new();

        public static int SegmentCount(string path) => Tokenize(path).Count;

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            List<object> a = Tokenize(x);
            List<object> b = Tokenize(y);
            int n = Math.Min(a.Count, b.Count);
            for (int i = 0; i < n; i++)
            {
                int cmp = CompareToken(a[i], b[i]);
                if (cmp != 0)
                {
                    return cmp;
                }
            }

            return a.Count.CompareTo(b.Count);
        }

        private static int CompareToken(object a, object b) => (a, b) switch
        {
            (string sa, string sb) => string.CompareOrdinal(sa, sb),
            (int ia, int ib) => ia.CompareTo(ib),
            (string, int) => -1,
            (int, string) => 1,
            _ => 0,
        };

        private static List<object> Tokenize(string path)
        {
            var tokens = new List<object>();
            int i = path.StartsWith("$", StringComparison.Ordinal) ? 1 : 0;
            var sb = new StringBuilder();
            void Flush()
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
            }

            while (i < path.Length)
            {
                char c = path[i];
                if (c == '.')
                {
                    Flush();
                    i++;
                }
                else if (c == '[')
                {
                    Flush();
                    int close = path.IndexOf(']', i);
                    if (close < 0)
                    {
                        sb.Append(c);
                        i++;
                        continue;
                    }

                    if (int.TryParse(path.AsSpan(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int idx))
                    {
                        tokens.Add(idx);
                    }

                    i = close + 1;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }

            Flush();
            return tokens;
        }
    }
}

/// <summary>
/// 命令行入口：读取协议包 JSON 文件，执行审计并把结果 JSON 写到标准输出。
/// 审计出问题时退出码仍为 0；仅在输入无法读取或解析时返回 2。
/// </summary>
public static class Program
{
    /// <summary>程序入口。参数可选：协议包 JSON 文件路径，缺省为 <c>materials/agreements.json</c>。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>退出码：0 = 审计完成；2 = 输入错误。</returns>
    public static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : Path.Combine("materials", "agreements.json");
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"无法读取输入文件 '{path}'：{ex.Message}");
            return 2;
        }

        try
        {
            AuditResult result = AgreementAuditor.Audit(json);
            Console.Out.WriteLine(result.ToJson());
            return 0;
        }
        catch (AuditInputException ex)
        {
            Console.Error.WriteLine($"输入无效：{ex.Message}");
            return 2;
        }
    }
}
