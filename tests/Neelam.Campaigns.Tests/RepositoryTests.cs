using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// A connection string may be stored where nobody would expect it to be passed on, such as an App
/// Service setting, but never in anything that reaches GitHub. This scans every file git would
/// commit: tracked files and untracked ones not ignored. It looks for real-looking values, so the
/// placeholders in tests (all-zero keys, <c>abc</c>, <c>x</c>) pass and a pasted real one does not.
/// It cannot see PR descriptions, issue comments or history that is already pushed.
/// </summary>
public class RepositoryTests
{
    private static readonly (string Name, Regex Pattern)[] RealLookingValues =
    [
        ("Application Insights instrumentation key",
            new Regex(@"(?i)(InstrumentationKey|ApplicationId)=(?!0{8}-0{4}-0{4}-0{4}-0{12})[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")),
        ("account key", new Regex(@"(?i)AccountKey=[A-Za-z0-9+/]{40,}")),
        ("shared access key", new Regex(@"(?i)SharedAccessKey=[A-Za-z0-9+/]{20,}")),
        ("SAS signature", new Regex(@"(?i)[?&;]sig=[A-Za-z0-9%+/]{20,}")),
    ];

    [Fact]
    public void Nothing_git_would_commit_holds_a_real_connection_string()
    {
        var leaks = CommittableFiles()
            .SelectMany(file => Leaks(File.ReadAllText(Path.Combine(InfrastructureTests.Root, file)))
                .Select(what => $"{file}: {what}"))
            .ToList();

        // Names the file and what was found, never the value.
        Assert.True(leaks.Count == 0, "Remove from the repository: " + string.Join("; ", leaks));
    }

    // The values are built here rather than written out, so this file does not trip the scan.
    [Fact]
    public void The_scan_finds_real_looking_values()
    {
        Assert.NotEmpty(Leaks($"InstrumentationKey={Guid.NewGuid()};IngestionEndpoint=https://x/"));
        Assert.NotEmpty(Leaks("AccountName=x;AccountKey=" + new string('k', 86) + "=="));
        Assert.NotEmpty(Leaks("Endpoint=sb://x/;SharedAccessKey=" + new string('k', 43) + "="));
        Assert.NotEmpty(Leaks("https://x.blob.core.windows.net/c?sv=2024-01-01&sig=" + new string('s', 44)));
    }

    [Fact]
    public void The_scan_passes_placeholders() =>
        Assert.Empty(Leaks("InstrumentationKey=00000000-0000-0000-0000-000000000000;AccountKey=abc==;sig=abc"));

    [Fact]
    public void The_scan_covers_the_files_that_matter()
    {
        var files = CommittableFiles();

        Assert.Contains("infra/main.bicep", files);
        Assert.Contains("infra/site.bicep", files);
        Assert.Contains("infra/storage.bicep", files);
        Assert.Contains("src/Neelam.Web/appsettings.json", files);
        Assert.Contains("src/Neelam.Web/Properties/launchSettings.json", files);
    }

    private static IEnumerable<string> Leaks(string text) =>
        RealLookingValues.Where(v => v.Pattern.IsMatch(text)).Select(v => v.Name);

    // A check that could not run is not a check that passed: no git means a failure, not a pass.
    private static List<string> CommittableFiles()
    {
        var git = new ProcessStartInfo("git", "ls-files -z --cached --others --exclude-standard")
        {
            WorkingDirectory = InfrastructureTests.Root,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(git) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);

        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(f => File.Exists(Path.Combine(InfrastructureTests.Root, f)))
            .ToList();
    }
}
