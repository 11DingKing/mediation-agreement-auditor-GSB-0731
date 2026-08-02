using MediationAgreementAuditor;

var path = args.Length > 0 ? args[0] : "materials/agreements.json";
if (!File.Exists(path))
{
    Console.Error.WriteLine($"Agreement file not found: {path}");
    return 2;
}

AuditResult result;
try
{
    result = AgreementAuditor.AuditFile(path);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to audit '{path}': {ex.Message}");
    return 2;
}

Console.WriteLine($"Agreement : {result.AgreementId}");
Console.WriteLine($"Issues    : {result.Issues.Count} (errors: {result.Issues.Count(i => i.Severity == Severity.Error)}, warnings: {result.Issues.Count(i => i.Severity == Severity.Warning)})");
Console.WriteLine();

if (result.Issues.Count == 0)
{
    Console.WriteLine("No consistency issues found.");
    return 0;
}

var header = $"{"CODE",-32} {"SEV",-7} {"EVIDENCE PATH",-48} MESSAGE";
Console.WriteLine(header);
Console.WriteLine(new string('-', header.Length));

foreach (var issue in result.Issues)
{
    var ev = issue.EvidencePath.Length > 48 ? "..." + issue.EvidencePath[^45..] : issue.EvidencePath;
    Console.WriteLine($"{issue.Code,-32} {issue.Severity,-7} {ev,-48} {issue.Message}");
}

return result.HasErrors ? 1 : 0;
