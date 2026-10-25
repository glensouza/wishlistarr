using System.Diagnostics;
using System.Text.Json;

namespace Wishlistarr.Tests;

public sealed class LicenseNoticeTests
{
    private static readonly Lazy<Task<bool>> ToolRestored = new(RestoreToolAsync);

    [Fact]
    public void Notices_are_written_when_publishing_and_not_on_every_build()
    {
        string root = RepoRoot();
        string targets = File.ReadAllText(Path.Combine(root, "build", "LicenseNotices.targets"));
        Assert.Contains("nuget-license", targets, StringComparison.Ordinal);
        Assert.Contains("--exclude-publish-false", targets, StringComparison.Ordinal);
        Assert.Contains("allowed-licenses.json", targets, StringComparison.Ordinal);
        Assert.Contains("WriteNuGetNotices.cs", targets, StringComparison.Ordinal);
        Assert.Contains("AfterTargets=\"Publish\"", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("BeforeBuild", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolvedFileToPublish", targets, StringComparison.Ordinal);

        string project = File.ReadAllText(Path.Combine(root, "src", "Wishlistarr.Api", "Wishlistarr.Api.csproj"));
        Assert.Contains("LicenseNotices.targets", project, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GPL-2.0-only", false)]
    [InlineData("GPL-2.0-only OR MIT", true)]
    [InlineData("MIT AND Apache-2.0", true)]
    [InlineData("GPL-2.0-only AND MIT", false)]
    public async Task Nuget_license_applies_the_allowlist_to_license_expressions(string expression, bool accepted)
    {
        if (!await ToolRestored.Value)
        {
            // The tool package cannot be restored without a network or a populated NuGet cache.
            return;
        }

        string root = RepoRoot();
        List<OverridePackage> overrides = [];
        foreach (string project in new[] { "Api", "Core", "Data", "Integrations" })
        {
            string assets = Path.Combine(root, "src", $"Wishlistarr.{project}", "obj", "project.assets.json");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
            foreach (JsonProperty library in document.RootElement.GetProperty("libraries").EnumerateObject())
            {
                if (library.Value.GetProperty("type").GetString() != "package")
                {
                    continue;
                }

                int slash = library.Name.IndexOf('/', StringComparison.Ordinal);
                string id = library.Name[..slash];
                string version = library.Name[(slash + 1)..];
                string license = id == "Serilog" ? expression : "MIT";
                if (!overrides.Exists(existing => existing.Id == id && existing.Version == version))
                {
                    overrides.Add(new OverridePackage(id, version, license));
                }
            }
        }

        Assert.Contains(overrides, package => package.Id == "Serilog");
        string temp = Directory.CreateTempSubdirectory("wishlistarr-license").FullName;
        try
        {
            string over = Path.Combine(temp, "override.json");
            File.WriteAllText(over, JsonSerializer.Serialize(overrides));
            ProcessStartInfo start = Tool(root);
            start.ArgumentList.Add("-ji");
            start.ArgumentList.Add("build/license-inputs.json");
            start.ArgumentList.Add("-t");
            start.ArgumentList.Add("--exclude-publish-false");
            start.ArgumentList.Add("-a");
            start.ArgumentList.Add("build/allowed-licenses.json");
            start.ArgumentList.Add("-override");
            start.ArgumentList.Add(over);
            start.ArgumentList.Add("-err");
            (int exitCode, string stdout, string stderr) = await RunAsync(start);
            string output = stdout + stderr;
            if (accepted)
            {
                Assert.True(exitCode == 0, output);
            }
            else
            {
                Assert.NotEqual(0, exitCode);
                Assert.Contains("Serilog", output, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public async Task Normalizer_writes_sorted_notices_and_rejects_a_blank_license()
    {
        string root = RepoRoot();
        string temp = Directory.CreateTempSubdirectory("wishlistarr-notices").FullName;
        try
        {
            string input = Path.Combine(temp, "raw.json");
            string output = Path.Combine(temp, "nuget.json");
            File.WriteAllText(
                input,
                """
                [
                  {"PackageId":"Zed","PackageVersion":"2.0.0","License":"MIT"},
                  {"PackageId":"Alpha","PackageVersion":"1.0.0","License":"Apache-2.0","ValidationErrors":[]}
                ]
                """);
            (int exitCode, _, string stderr) = await RunAsync(Normalizer(root, input, output));
            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(output));
            JsonElement packages = document.RootElement.GetProperty("packages");
            Assert.Equal("Alpha", packages[0].GetProperty("name").GetString());
            Assert.Equal("Apache-2.0", packages[0].GetProperty("license").GetString());
            Assert.Equal("Zed", packages[1].GetProperty("name").GetString());

            File.WriteAllText(output, """{"packages":[{"name":"stale","version":"0","license":"MIT"}]}""");
            File.WriteAllText(
                input,
                """[{"PackageId":"Example","PackageVersion":"1.0.0","License":" "}]""");
            (exitCode, _, stderr) = await RunAsync(Normalizer(root, input, output));
            Assert.NotEqual(0, exitCode);
            Assert.Contains("blank", stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static async Task<bool> RestoreToolAsync()
    {
        ProcessStartInfo start = new()
        {
            FileName = "dotnet",
            WorkingDirectory = RepoRoot(),
        };
        start.Environment["SkipLicenseCheck"] = "true";
        start.ArgumentList.Add("tool");
        start.ArgumentList.Add("restore");
        (int exitCode, _, _) = await RunAsync(start);
        return exitCode == 0;
    }

    private static ProcessStartInfo Tool(string root)
    {
        ProcessStartInfo start = new()
        {
            FileName = "dotnet",
            WorkingDirectory = root,
        };
        start.Environment["SkipLicenseCheck"] = "true";
        start.Environment["HTTP_PROXY"] = "http://127.0.0.1:9";
        start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:9";
        start.Environment["http_proxy"] = "http://127.0.0.1:9";
        start.Environment["https_proxy"] = "http://127.0.0.1:9";
        start.ArgumentList.Add("tool");
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("nuget-license");
        start.ArgumentList.Add("--");
        return start;
    }

    private static ProcessStartInfo Normalizer(string root, string input, string output)
    {
        ProcessStartInfo start = new()
        {
            FileName = "dotnet",
            WorkingDirectory = root,
        };
        start.Environment["SkipLicenseCheck"] = "true";
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--file");
        start.ArgumentList.Add(Path.Combine(root, "build", "WriteNuGetNotices.cs"));
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(output);
        return start;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(ProcessStartInfo start)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the process.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(180));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wishlistarr.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }

    private sealed record OverridePackage(string Id, string Version, string License);
}
