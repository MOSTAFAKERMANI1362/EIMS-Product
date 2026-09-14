using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EIMS.HrImport;

public static class CanonicalCsv
{
    public static IReadOnlyCollection<string[]> ParseRows(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (ch == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row.Clear();
            }
            else if (ch == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row.Clear();
            }
            else
            {
                field.Append(ch);
            }
        }

        if (quoted)
            throw new FormatException("P4_CSV_UNCLOSED_QUOTE");

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows
            .Where(x => x.Any(v => !string.IsNullOrWhiteSpace(v)))
            .ToArray();
    }

    public static (IReadOnlyCollection<HrOrgRecord> Records, IReadOnlyCollection<ImportIssue> Issues) ParseCanonical(string text)
    {
        var issues = new List<ImportIssue>();
        var rows = ParseRows(text).ToArray();
        if (rows.Length == 0)
            return (Array.Empty<HrOrgRecord>(), new[] { new ImportIssue(null, "P4_EMPTY_FILE", ImportIssueSeverity.Error) });

        var header = rows[0].Select((x, i) => i == 0 ? x.TrimStart('\uFEFF').Trim() : x.Trim()).ToArray();
        if (!header.SequenceEqual(P4CanonicalContract.Columns, StringComparer.Ordinal))
        {
            issues.Add(new ImportIssue(1, "P4_HEADER_MISMATCH", ImportIssueSeverity.Error));
            return (Array.Empty<HrOrgRecord>(), issues);
        }

        var records = new List<HrOrgRecord>();
        for (var index = 1; index < rows.Length; index++)
        {
            var rowNumber = index + 1;
            var values = rows[index];
            if (values.Length != P4CanonicalContract.Columns.Length)
            {
                issues.Add(new ImportIssue(rowNumber, "P4_COLUMN_COUNT_MISMATCH", ImportIssueSeverity.Error));
                continue;
            }

            var normalized = values.Select(x => x.Trim()).ToArray();
            if (!Enum.TryParse<CanonicalEmploymentStatus>(normalized[7], ignoreCase: false, out var status))
            {
                issues.Add(new ImportIssue(rowNumber, "P4_INVALID_EMPLOYMENT_STATUS", ImportIssueSeverity.Error, "EmploymentStatus"));
                continue;
            }

            if (!DateOnly.TryParseExact(normalized[8], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var effectiveFrom))
            {
                issues.Add(new ImportIssue(rowNumber, "P4_INVALID_EFFECTIVE_DATE", ImportIssueSeverity.Error, "EffectiveFrom"));
                continue;
            }

            records.Add(new HrOrgRecord(
                normalized[0],
                normalized[1],
                normalized[2],
                normalized[3],
                normalized[4],
                normalized[5],
                string.IsNullOrWhiteSpace(normalized[6]) ? null : normalized[6],
                status,
                effectiveFrom,
                rowNumber));
        }

        return (records.AsReadOnly(), issues.AsReadOnly());
    }

    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
