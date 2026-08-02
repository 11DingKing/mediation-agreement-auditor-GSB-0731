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

    /// <summary>两份生效补充协议替换同一条款，后出现的补充协议构成重复替换。</summary>
    public const string AmendmentConflict = "MED_AMENDMENT_CONFLICT";

    /// <summary>协议声明的金额总额与全部生效条款金额之和不一致。</summary>
    public const string AmountMismatch = "MED_AMOUNT_MISMATCH";

    /// <summary>条款的履行日期早于其所依赖（引用）条款的生效履行日期。</summary>
    public const string DateOrder = "MED_DATE_ORDER";

    /// <summary>条款引用关系在生效图上构成有向环。</summary>
    public const string ReferenceCycle = "MED_REFERENCE_CYCLE";

    /// <summary>当事人的签署范围缺少协议本体或某份已生效补充协议。</summary>
    public const string SignatureScopeGap = "MED_SIGNATURE_SCOPE_GAP";

    /// <summary>签署范围中出现了非当前有效文件（未知编号、已撤回或未生效的补充协议）。</summary>
    public const string SignatureScopeUnknown = "MED_SIGNATURE_SCOPE_UNKNOWN";

    /// <summary>引用仍指向已被生效补充协议替换的条款，应改指向替换后的条款。</summary>
    public const string StaleReference = "MED_STALE_REFERENCE";
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
/// 补充协议。对应 JSON 中 <c>amendments</c> 数组的元素；
/// 生效时将 <see cref="Replaces"/> 指定的条款替换为编号为 <see cref="NewClauseId"/> 的新条款。
/// </summary>
public sealed class Amendment
{
    /// <summary>补充协议编号，在协议包内唯一。</summary>
    public required string Id { get; init; }

    /// <summary>被替换的条款编号（可以是基础条款，也可以是先前补充协议产生的条款）。</summary>
    public required string Replaces { get; init; }

    /// <summary>替换后新条款的编号；缺省时表示就地修改原条款。</summary>
    public string? NewClauseId { get; init; }

    /// <summary>替换后的金额（分）；为 <c>null</c> 时继承被替换条款的金额。</summary>
    public long? AmountFen { get; init; }

    /// <summary>替换后的履行日期；为 <c>null</c> 时继承被替换条款的履行日期。</summary>
    public DateOnly? Due { get; init; }

    /// <summary>是否已撤回。已撤回的补充协议不产生任何替换效果，也不要求被签署。</summary>
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

    /// <summary>补充协议列表，按声明顺序依次生效。</summary>
    public IReadOnlyList<Amendment> Amendments { get; init; } = Array.Empty<Amendment>();

    /// <summary>签署记录列表。</summary>
    public IReadOnlyList<Signature> Signatures { get; init; } = Array.Empty<Signature>();

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
                string? replaces = GetString(el, "replaces");
                if (id is null || replaces is null)
                {
                    continue;
                }

                list.Add(new Amendment
                {
                    Id = id,
                    Replaces = replaces,
                    NewClauseId = GetString(el, "newClauseId"),
                    AmountFen = GetInt64(el, "amountFen"),
                    Due = GetDate(el, "due"),
                    Withdrawn = TryGetProperty(el, "withdrawn", out JsonElement w)
                        && (w.ValueKind == JsonValueKind.True || w.ValueKind == JsonValueKind.False)
                        && w.GetBoolean(),
                });
            }
        }

        return list;
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
    /// </summary>
    public required string EvidencePath { get; init; }
}

/// <summary>
/// 审计结果：协议编号与按稳定顺序排列的全部问题。
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
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// 调解协议一致性审计器。所有图遍历（引用链追踪、有向环检测）均以显式栈迭代实现，
/// 任意深度的引用链与随机协议图都不会导致栈溢出；审计过程无副作用，结果完全确定。
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
    /// 对协议包执行全部一致性检查：悬空引用、补充协议替换、签署范围、金额一致性、
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

    /// <summary>生效条款：基础条款经生效补充协议替换后的视图，同时记录每个取值在原始 JSON 中的出处。</summary>
    private sealed class EffectiveClause
    {
        public required string Id { get; init; }

        public string? ObligorId { get; set; }

        /// <summary>义务人取值在原始 JSON 中的路径（始终来自基础条款）。</summary>
        public string? ObligorPath { get; set; }

        public long? AmountFen { get; set; }

        public DateOnly? Due { get; set; }

        /// <summary>履行日期取值在原始 JSON 中的路径（来自基础条款或补充协议）。</summary>
        public string? DuePath { get; set; }

        /// <summary>引用边（原始目标编号 + 原始 JSON 路径），随替换沿继承关系保留。</summary>
        public List<RefEdge> Refs { get; } = new();

        /// <summary>文档顺序：基础条款在前，补充协议产生的条款在后。</summary>
        public int Order { get; init; }
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

    private sealed class Engine
    {
        private readonly Agreement _agreement;
        private readonly List<AuditIssue> _issues = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Dictionary<string, EffectiveClause> _effectiveById = new(StringComparer.Ordinal);
        private readonly List<EffectiveClause> _effectiveInOrder = new();
        private readonly Dictionary<string, string> _replacedBy = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _chainNext = new(StringComparer.Ordinal);
        private readonly List<string> _effectiveAmendmentIds = new();

        public Engine(Agreement agreement) => _agreement = agreement;

        public AuditResult Run()
        {
            LoadBaseClauses();
            ApplyAmendments();
            Dictionary<string, string> chainFinal = BuildChainFinalMap();
            CheckReferences(chainFinal);
            CheckDateOrder(chainFinal);
            CheckAmounts();
            CheckSignatures();
            CheckCycles(chainFinal);

            List<AuditIssue> sorted = _issues
                .OrderBy(i => i.Code, StringComparer.Ordinal)
                .ThenBy(i => i.EvidencePath, EvidencePathComparer.Instance)
                .ThenBy(i => i.Message, StringComparer.Ordinal)
                .ToList();

            return new AuditResult { AgreementId = _agreement.Id, Issues = sorted };
        }

        private void Emit(string code, IssueSeverity severity, string message, string evidencePath)
        {
            // 同一问题（代码+路径+文案）只报告一次：重复提交的相同内容不会放大结果。
            if (_seen.Add(string.Concat(code, "", evidencePath, "", message)))
            {
                _issues.Add(new AuditIssue { Code = code, Severity = severity, Message = message, EvidencePath = evidencePath });
            }
        }

        private void LoadBaseClauses()
        {
            for (int i = 0; i < _agreement.Clauses.Count; i++)
            {
                Clause c = _agreement.Clauses[i];
                if (_effectiveById.ContainsKey(c.Id))
                {
                    continue; // 编号重复时以先出现者为准，行为确定。
                }

                var ec = new EffectiveClause
                {
                    Id = c.Id,
                    ObligorId = c.Obligation.ObligorId,
                    ObligorPath = c.Obligation.ObligorId is null ? null : $"$.clauses[{i}].obligor",
                    AmountFen = c.Obligation.AmountFen,
                    Due = c.Obligation.Due,
                    DuePath = c.Obligation.Due is null ? null : $"$.clauses[{i}].due",
                    Order = i,
                };
                for (int j = 0; j < c.References.Count; j++)
                {
                    ec.Refs.Add(new RefEdge(c.References[j], $"$.clauses[{i}].references[{j}]"));
                }

                _effectiveById.Add(c.Id, ec);
                _effectiveInOrder.Add(ec);
            }
        }

        private void ApplyAmendments()
        {
            for (int i = 0; i < _agreement.Amendments.Count; i++)
            {
                Amendment a = _agreement.Amendments[i];
                if (a.Withdrawn)
                {
                    continue; // 已撤回：无效果、不要求签署；其产物视为不存在。
                }

                string replacesPath = $"$.amendments[{i}].replaces";
                if (_replacedBy.ContainsKey(a.Replaces))
                {
                    Emit(
                        IssueCodes.AmendmentConflict,
                        IssueSeverity.Error,
                        $"条款 '{a.Replaces}' 已被补充协议 '{_replacedBy[a.Replaces]}' 替换，'{a.Id}' 构成重复替换。",
                        replacesPath);
                    continue;
                }

                if (!_effectiveById.TryGetValue(a.Replaces, out EffectiveClause? target))
                {
                    Emit(
                        IssueCodes.AmendmentTargetUnknown,
                        IssueSeverity.Error,
                        $"补充协议 '{a.Id}' 要替换的条款 '{a.Replaces}' 不存在（或产生它的补充协议已撤回）。",
                        replacesPath);
                    continue;
                }

                string newId = string.IsNullOrEmpty(a.NewClauseId) ? a.Replaces : a.NewClauseId!;
                var replacement = new EffectiveClause
                {
                    Id = newId,
                    ObligorId = target.ObligorId,
                    ObligorPath = target.ObligorPath,
                    AmountFen = a.AmountFen ?? target.AmountFen,
                    Due = a.Due ?? target.Due,
                    DuePath = a.Due is not null ? $"$.amendments[{i}].due" : target.DuePath,
                    Order = _agreement.Clauses.Count + i,
                };
                replacement.Refs.AddRange(target.Refs);

                _effectiveById.Remove(a.Replaces);
                _effectiveById[newId] = replacement;
                _effectiveInOrder.Remove(target);
                _effectiveInOrder.Add(replacement);
                _replacedBy[a.Replaces] = a.Id;
                _chainNext[a.Replaces] = newId;
                _effectiveAmendmentIds.Add(a.Id);
            }
        }

        /// <summary>把每个被替换编号沿替换链迭代追踪到最终生效编号；visited 集合防止异常链条造成死循环。</summary>
        private Dictionary<string, string> BuildChainFinalMap()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string replaced in _chainNext.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                string current = replaced;
                var visited = new HashSet<string>(StringComparer.Ordinal) { current };
                while (_chainNext.TryGetValue(current, out string? next) && visited.Add(next))
                {
                    current = next;
                }

                map[replaced] = current;
            }

            return map;
        }

        private void CheckReferences(Dictionary<string, string> chainFinal)
        {
            foreach (EffectiveClause clause in _effectiveInOrder.OrderBy(c => c.Order))
            {
                // 义务人必须是已知当事人。
                if (clause.ObligorId is not null
                    && !_agreement.Parties.Any(p => p.Id == clause.ObligorId)
                    && clause.ObligorPath is not null)
                {
                    Emit(
                        IssueCodes.UnknownReference,
                        IssueSeverity.Error,
                        $"条款 '{clause.Id}' 的义务人 '{clause.ObligorId}' 不在当事人列表中。",
                        clause.ObligorPath);
                }

                foreach (RefEdge edge in clause.Refs)
                {
                    if (_effectiveById.ContainsKey(edge.RawTarget))
                    {
                        continue;
                    }

                    if (chainFinal.TryGetValue(edge.RawTarget, out string? final) && _effectiveById.ContainsKey(final))
                    {
                        Emit(
                            IssueCodes.StaleReference,
                            IssueSeverity.Warning,
                            $"引用仍指向已被补充协议 '{_replacedBy[edge.RawTarget]}' 替换的条款 '{edge.RawTarget}'，应改指向 '{final}'。",
                            edge.Path);
                    }
                    else
                    {
                        Emit(
                            IssueCodes.UnknownReference,
                            IssueSeverity.Error,
                            $"条款 '{clause.Id}' 引用了不存在的条款 '{edge.RawTarget}'。",
                            edge.Path);
                    }
                }
            }
        }

        private void CheckDateOrder(Dictionary<string, string> chainFinal)
        {
            foreach (EffectiveClause clause in _effectiveInOrder.OrderBy(c => c.Order))
            {
                if (clause.Due is null || clause.DuePath is null)
                {
                    continue;
                }

                foreach (RefEdge edge in clause.Refs)
                {
                    string targetId = _effectiveById.ContainsKey(edge.RawTarget)
                        ? edge.RawTarget
                        : chainFinal.TryGetValue(edge.RawTarget, out string? f) ? f : edge.RawTarget;
                    if (!_effectiveById.TryGetValue(targetId, out EffectiveClause? target) || target.Due is null)
                    {
                        continue;
                    }

                    if (clause.Due.Value < target.Due.Value)
                    {
                        Emit(
                            IssueCodes.DateOrder,
                            IssueSeverity.Error,
                            $"条款 '{clause.Id}' 的履行日期 {clause.Due.Value:yyyy-MM-dd} 早于其依赖条款 '{targetId}' 的履行日期 {target.Due.Value:yyyy-MM-dd}。",
                            clause.DuePath);
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
            foreach (EffectiveClause clause in _effectiveInOrder)
            {
                sum += clause.AmountFen ?? 0;
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

        private void CheckSignatures()
        {
            var partyIds = new HashSet<string>(_agreement.Parties.Select(p => p.Id), StringComparer.Ordinal);

            // 有效签署范围：协议本体 + 已生效补充协议。
            var validScope = new HashSet<string>(StringComparer.Ordinal) { _agreement.Id };
            foreach (string id in _effectiveAmendmentIds)
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
                            $"签署范围条目 '{sig.Scope[j]}' 不是当前有效的协议或补充协议编号（可能为未知编号或已撤回/未生效）。",
                            $"$.signatures[{i}].scope[{j}]");
                    }
                }
            }

            for (int i = 0; i < _agreement.Parties.Count; i++)
            {
                Party party = _agreement.Parties[i];
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

        private void CheckCycles(Dictionary<string, string> chainFinal)
        {
            // 邻接表：边 = 引用（解析到生效条款），按文档顺序，携带原始 JSON 路径。
            var adjacency = new Dictionary<string, List<(string Target, string Path)>>(StringComparer.Ordinal);
            List<EffectiveClause> nodes = _effectiveInOrder.OrderBy(c => c.Order).ToList();
            foreach (EffectiveClause clause in nodes)
            {
                var edges = new List<(string, string)>();
                foreach (RefEdge edge in clause.Refs)
                {
                    string targetId = _effectiveById.ContainsKey(edge.RawTarget)
                        ? edge.RawTarget
                        : chainFinal.TryGetValue(edge.RawTarget, out string? f) ? f : edge.RawTarget;
                    if (_effectiveById.ContainsKey(targetId))
                    {
                        edges.Add((targetId, edge.Path));
                    }
                }

                adjacency[clause.Id] = edges;
            }

            foreach (List<string> scc in FindStronglyConnectedComponents(nodes.Select(n => n.Id), adjacency))
            {
                var members = new HashSet<string>(scc, StringComparer.Ordinal);
                string? bestPath = null;
                foreach (EffectiveClause clause in nodes)
                {
                    if (!members.Contains(clause.Id))
                    {
                        continue;
                    }

                    foreach ((string target, string path) in adjacency[clause.Id])
                    {
                        if (members.Contains(target) && (bestPath is null || EvidencePathComparer.Instance.Compare(path, bestPath) < 0))
                        {
                            bestPath = path;
                        }
                    }
                }

                if (bestPath is not null)
                {
                    string memberList = string.Join("、", nodes.Where(n => members.Contains(n.Id)).Select(n => n.Id));
                    Emit(
                        IssueCodes.ReferenceCycle,
                        IssueSeverity.Error,
                        $"条款引用构成有向环，涉及：{memberList}。",
                        bestPath);
                }
            }
        }

        /// <summary>迭代式 Tarjan 强连通分量算法：显式栈代替递归，任意深度的图都不会栈溢出。</summary>
        private static List<List<string>> FindStronglyConnectedComponents(
            IEnumerable<string> nodeIds,
            Dictionary<string, List<(string Target, string Path)>> adjacency)
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var low = new Dictionary<string, int>(StringComparer.Ordinal);
            var onStack = new HashSet<string>(StringComparer.Ordinal);
            var stack = new List<string>();
            var result = new List<List<string>>();
            var inLargeScc = new HashSet<string>(StringComparer.Ordinal);
            int counter = 0;

            foreach (string start in nodeIds)
            {
                if (index.ContainsKey(start))
                {
                    continue;
                }

                var callStack = new Stack<(string Node, int NextChild)>();
                index[start] = low[start] = counter++;
                stack.Add(start);
                onStack.Add(start);
                callStack.Push((start, 0));

                while (callStack.Count > 0)
                {
                    (string v, int next) = callStack.Pop();
                    List<(string Target, string Path)> children = adjacency[v];
                    if (next < children.Count)
                    {
                        callStack.Push((v, next + 1));
                        string w = children[next].Target;
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
                            var scc = new List<string>();
                            string w;
                            do
                            {
                                w = stack[^1];
                                stack.RemoveAt(stack.Count - 1);
                                onStack.Remove(w);
                                scc.Add(w);
                            }
                            while (w != v);

                            if (scc.Count > 1)
                            {
                                result.Add(scc);
                                foreach (string m in scc)
                                {
                                    inLargeScc.Add(m);
                                }
                            }
                        }

                        if (callStack.Count > 0)
                        {
                            string parent = callStack.Peek().Node;
                            low[parent] = Math.Min(low[parent], low[v]);
                        }
                    }
                }
            }

            // 自环：节点引用自身且不属于更大的环。
            foreach (KeyValuePair<string, List<(string Target, string Path)>> pair in adjacency)
            {
                if (!inLargeScc.Contains(pair.Key) && pair.Value.Any(e => e.Target == pair.Key))
                {
                    result.Add(new List<string> { pair.Key });
                }
            }

            return result;
        }
    }

    /// <summary>
    /// 证据路径比较器：把 <c>$.clauses[10].references[2]</c> 拆成属性段与下标段逐段比较，
    /// 数组下标按数值而非字符串比较，保证 <c>clauses[2]</c> 排在 <c>clauses[10]</c> 之前。
    /// </summary>
    private sealed class EvidencePathComparer : IComparer<string>
    {
        public static readonly EvidencePathComparer Instance = new();

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
