using System.Diagnostics;
using System.Text.Json;

namespace Wishlistarr.Tests;

public sealed class LicenseNoticeTests
{
    [Fact]
    public void Build_writes_nuget_license_notices()
    {
        string root = RepoRoot();
        string targets = File.ReadAllText(Path.Combine(root, "build", "LicenseNotices.targets"));
        Assert.Contains("nuget-license", targets, StringComparison.Ordinal);
        Assert.Contains("--exclude-publish-false", targets, StringComparison.Ordinal);
        Assert.Contains("allowed-licenses.json", targets, StringComparison.Ordinal);
        Assert.Contains("WriteNuGetNotices.cs", targets, StringComparison.Ordinal);

        string path = Path.Combine(root, "web", "public", "licenses", "nuget.json");
        string wwwroot = Path.Combine(root, "src", "Wishlistarr.Api", "wwwroot", "licenses", "nuget.json");
        Assert.True(File.Exists(path), path);
        Assert.True(File.Exists(wwwroot), wwwroot);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement packages = document.RootElement.GetProperty("packages");
        Assert.True(packages.GetArrayLength() > 0);
        bool serilog = false;
        bool scalar = false;
        foreach (JsonElement package in packages.EnumerateArray())
        {
            string? name = package.GetProperty("name").GetString();
            string? version = package.GetProperty("version").GetString();
            string? license = package.GetProperty("license").GetString();
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.False(string.IsNullOrWhiteSpace(version));
            Assert.False(string.IsNullOrWhiteSpace(license));
            Assert.DoesNotContain("PackageId", package.ToString(), StringComparison.Ordinal);
            if (name == "Serilog")
            {
                Assert.Equal("Apache-2.0", license);
                serilog = true;
            }

            if (name == "Scalar.AspNetCore")
            {
                Assert.Equal("MIT", license);
                scalar = true;
            }
        }

        Assert.True(serilog);
        Assert.True(scalar);
        Assert.DoesNotContain("PackageId", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nuget_license_rejects_an_incompatible_package()
    {
        string root = RepoRoot();
        string assets = Path.Combine(root, "src", "Wishlistarr.Api", "obj", "project.assets.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
        List<OverridePackage> overrides = [];
        foreach (JsonProperty library in document.RootElement.GetProperty("libraries").EnumerateObject())
        {
            const string prefix = "Serilog/";
            if (!library.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            overrides.Add(new OverridePackage("Serilog", library.Name[prefix.Length..], "GPL-2.0-only"));
        }

        Assert.NotEmpty(overrides);
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
            Assert.NotEqual(0, exitCode);
            Assert.Contains("GPL-2.0-only", output, StringComparison.Ordinal);
            Assert.Contains("Serilog", output, StringComparison.Ordinal);
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

    private static ProcessStartInfo Tool(string root)
    {
        ProcessStartInfo start = new()
        {
            FileName = "dotnet",
            WorkingDirectory = root,
        };
        start.Environment["SkipLicenseCheck"] = "true";
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
