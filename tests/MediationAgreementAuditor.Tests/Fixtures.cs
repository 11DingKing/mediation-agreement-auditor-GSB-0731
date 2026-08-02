using System.Text.Json;
using MediationAgreementAuditor;

namespace MediationAgreementAuditor.Tests;

internal static class Fixtures
{
    public static AgreementPackage ValidPackage()
    {
        return new AgreementPackage
        {
            AgreementId = "AGR-TEST",
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
            Amendments = new List<Amendment>(),
            Signatures = new List<Signature>
            {
                new()
                {
                    PartyId = "PTY-A", Scope = new List<string> { "AGR-TEST" },
                    SignedAt = "2026-08-01T10:00:00Z"
                },
                new()
                {
                    PartyId = "PTY-B", Scope = new List<string> { "AGR-TEST" },
                    SignedAt = "2026-08-01T10:01:00Z"
                }
            }
        };
    }

    public static JsonSerializerOptions WriteOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static AuditResult AuditRoundTrip(AgreementPackage package, out string json, out JsonDocument doc)
    {
        json = JsonSerializer.Serialize(package, WriteOptions);
        doc = JsonDocument.Parse(json);
        return AgreementAuditor.AuditJson(json);
    }

    public static bool EvidencePathExists(JsonDocument doc, string path)
    {
        if (!path.StartsWith("$."))
        {
            return false;
        }

        var current = doc.RootElement;
        var rest = path[2..];
        while (rest.Length > 0)
        {
            string segment;
            var dot = rest.IndexOf('.');
            if (dot < 0)
            {
                segment = rest;
                rest = string.Empty;
            }
            else
            {
                segment = rest[..dot];
                rest = rest[(dot + 1)..];
            }

            var bracket = segment.IndexOf('[');
            string property;
            int? index = null;
            if (bracket >= 0)
            {
                property = segment[..bracket];
                var close = segment.IndexOf(']');
                if (close <= bracket + 1)
                {
                    return false;
                }

                var num = segment[(bracket + 1)..close];
                if (!int.TryParse(num, out var parsed))
                {
                    return false;
                }
                index = parsed;
            }
            else
            {
                property = segment;
            }

            if (current.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            if (!current.TryGetProperty(property, out current))
            {
                return false;
            }
            if (index.HasValue)
            {
                if (current.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }
                if (index.Value < 0 || index.Value >= current.GetArrayLength())
                {
                    return false;
                }
                current = current[index.Value];
            }
        }

        return true;
    }
}
