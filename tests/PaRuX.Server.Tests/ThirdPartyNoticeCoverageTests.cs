using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ThirdPartyNoticeCoverageTests
{
    private static readonly string[] ExpectedMissingPackageNoticeRows =
    {
        "Microsoft.CodeAnalysis.CSharp",
        "Microsoft.ML.OnnxRuntimeGenAI.WinML",
        "Microsoft.Windows.AI.MachineLearning",
        "System.Management",
        "System.Security.Cryptography.ProtectedData"
    };

    private static readonly IReadOnlyDictionary<string, string> PackageNoticeExemptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.CodeAnalysis.CSharp"] =
                "Known notice-table gap retained for owner follow-up; this test-only task cannot edit the notice document, and this is not license clearance.",
            ["Microsoft.ML.OnnxRuntimeGenAI.WinML"] =
                "Known notice-table gap retained for owner follow-up; this test-only task cannot edit the notice document, and this is not license clearance.",
            ["Microsoft.Windows.AI.MachineLearning"] =
                "Known notice-table gap retained for owner follow-up; this test-only task cannot edit the notice document, and this is not license clearance.",
            ["System.Management"] =
                "Known notice-table gap retained for owner follow-up; this test-only task cannot edit the notice document, and this is not license clearance.",
            ["System.Security.Cryptography.ProtectedData"] =
                "Known notice-table gap retained for owner follow-up; this test-only task cannot edit the notice document, and this is not license clearance."
        };

    [Fact]
    public void Shipped_project_package_references_have_notice_rows_or_reasoned_exemptions()
    {
        string root = FindRepositoryRoot();
        HashSet<string> packageIds = ReadDirectPackageReferenceIds(root);
        IReadOnlyList<NoticeRow> noticeRows = ReadNoticeRows(root);
        var noticePackageIds = noticeRows
            .Select(row => row.PackageName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] packagesWithoutNoticeRows = packageIds
            .Where(packageId => !noticePackageIds.Contains(packageId))
            .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(
            ExpectedMissingPackageNoticeRows,
            packagesWithoutNoticeRows,
            StringComparer.OrdinalIgnoreCase);

        string[] uncoveredPackageIds = packageIds
            .Where(packageId =>
                !noticePackageIds.Contains(packageId) &&
                !PackageNoticeExemptions.ContainsKey(packageId))
            .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Empty(uncoveredPackageIds);

        Assert.Equal(
            ExpectedMissingPackageNoticeRows,
            PackageNoticeExemptions.Keys.OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (string packageId in packagesWithoutNoticeRows)
        {
            Assert.True(
                PackageNoticeExemptions.TryGetValue(packageId, out string? reason) &&
                !string.IsNullOrWhiteSpace(reason),
                $"Package '{packageId}' has no notice row and must have a reasoned explicit exemption.");
        }
    }

    [Fact]
    public void Every_notice_table_row_has_a_non_empty_license_or_terms_cell()
    {
        string root = FindRepositoryRoot();
        IReadOnlyList<NoticeRow> rows = ReadNoticeRows(root);

        Assert.NotEmpty(rows);
        foreach (NoticeRow row in rows)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(row.LicenseOrTerms),
                $"Notice row {row.LineNumber} for '{row.PackageName}' has an empty license/terms cell.");
        }
    }

    private static HashSet<string> ReadDirectPackageReferenceIds(string root)
    {
        string sourceDirectory = Path.Combine(root, "src");
        string[] projectFiles = Directory.EnumerateFiles(
                sourceDirectory,
                "*.csproj",
                SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(projectFiles);

        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string projectFile in projectFiles)
        {
            XDocument project = XDocument.Load(projectFile);
            foreach (XElement packageReference in project
                         .Descendants()
                         .Where(element => string.Equals(
                             element.Name.LocalName,
                             "PackageReference",
                             StringComparison.OrdinalIgnoreCase)))
            {
                string? packageId = packageReference.Attributes()
                    .FirstOrDefault(attribute => string.Equals(
                        attribute.Name.LocalName,
                        "Include",
                        StringComparison.OrdinalIgnoreCase))
                    ?.Value;
                Assert.False(
                    string.IsNullOrWhiteSpace(packageId),
                    $"PackageReference in '{Path.GetRelativePath(root, projectFile)}' must have a literal Include id.");
                packageIds.Add(packageId!.Trim());
            }
        }

        return packageIds;
    }

    private static IReadOnlyList<NoticeRow> ReadNoticeRows(string root)
    {
        string noticePath = Path.Combine(root, "THIRD_PARTY_NOTICES.md");
        string[] lines = File.ReadAllLines(noticePath);
        var rows = new List<NoticeRow>();

        for (int headerLine = 0; headerLine + 1 < lines.Length; headerLine++)
        {
            if (!IsTableRow(lines[headerLine]))
                continue;

            string[] headers = ParseTableCells(lines[headerLine]);
            string[] separators = ParseTableCells(lines[headerLine + 1]);
            if (headers.Length == 0 || !separators.All(IsSeparatorCell))
                continue;

            int licenseColumn = Array.FindIndex(
                headers,
                header => header.Contains("license", StringComparison.OrdinalIgnoreCase) ||
                          header.Contains("licence", StringComparison.OrdinalIgnoreCase) ||
                          header.Contains("terms", StringComparison.OrdinalIgnoreCase));
            Assert.True(
                licenseColumn >= 0,
                $"Notice table at line {headerLine + 1} must name a license or terms column.");

            int rowLine = headerLine + 2;
            while (rowLine < lines.Length && IsTableRow(lines[rowLine]))
            {
                string[] cells = ParseTableCells(lines[rowLine]);
                Assert.Equal(headers.Length, cells.Length);
                rows.Add(new NoticeRow(
                    PackageName: cells[0],
                    LicenseOrTerms: cells[licenseColumn],
                    LineNumber: rowLine + 1));
                rowLine++;
            }

            headerLine = rowLine - 1;
        }

        return rows;
    }

    private static bool IsTableRow(string line) =>
        line.TrimStart().StartsWith('|') && line.TrimEnd().EndsWith('|');

    private static string[] ParseTableCells(string line) =>
        line.Trim().Trim('|')
            .Split('|', StringSplitOptions.None)
            .Select(cell => cell.Trim())
            .ToArray();

    private static bool IsSeparatorCell(string cell) =>
        Regex.IsMatch(cell, "^:?-{3,}:?$");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln")))
                return current.FullName;
            current = current.Parent;
        }

        current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate PaRuX.sln from the test process.");
    }

    private sealed record NoticeRow(string PackageName, string LicenseOrTerms, int LineNumber);
}
