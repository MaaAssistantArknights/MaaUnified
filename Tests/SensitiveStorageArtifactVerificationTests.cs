using System.Diagnostics;
using System.Xml.Linq;

namespace MAAUnified.Tests;

public sealed class SensitiveStorageArtifactVerificationTests
{
    [Fact]
    public async Task LinuxArtifactVerifierChecksAllDynamicElfFilesAndCreatesPrivateSmokeDirectories()
    {
        if (OperatingSystem.IsWindows()) return;

        var sourceRoot = FindSourceRoot();
        var verifier = Path.Combine(sourceRoot, "CI", "verify-sensitive-storage-artifact.sh");
        var testRoot = Path.Combine(Path.GetTempPath(), "maa-unified-artifact-test", Guid.NewGuid().ToString("N"));
        var publishDirectory = Path.Combine(testRoot, "publish");
        var executable = Path.Combine(publishDirectory, "bin", "MAAUnified");
        var toolsDirectory = Path.Combine(testRoot, "tools");
        var lddLog = Path.Combine(testRoot, "ldd.log");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        Directory.CreateDirectory(Path.Combine(publishDirectory, "native"));
        Directory.CreateDirectory(toolsDirectory);
        await WriteFakeElfAsync(Path.Combine(publishDirectory, "native", "libe_sqlcipher.so"));
        await WriteFakeElfAsync(Path.Combine(publishDirectory, "native", "lib MaaCore dependency.so"));
        await WriteFakeElfAsync(Path.Combine(publishDirectory, "native", "static helper"));
        await File.WriteAllTextAsync(Path.Combine(publishDirectory, "native", "not-an-elf.so"), "not an ELF file");
        await WriteExecutableAsync(
            executable,
            """
            #!/usr/bin/env bash
            set -euo pipefail
            runtime="$2"
            xdg="$3"
            [[ -d "$runtime" && -d "$xdg" ]]
            if [[ "$(uname -s)" == Darwin ]]; then
              [[ "$(stat -f '%Lp' "$runtime")" == 700 ]]
              [[ "$(stat -f '%Lp' "$xdg")" == 700 ]]
            else
              [[ "$(stat -c %a "$runtime")" == 700 ]]
              [[ "$(stat -c %a "$xdg")" == 700 ]]
            fi
            mkdir -p "$runtime/data"
            touch "$runtime/data/sensitive-settings.db"
            echo cipher_version=test
            """);
        await WriteExecutableAsync(
            Path.Combine(toolsDirectory, "readelf"),
            """
            #!/usr/bin/env bash
            set -euo pipefail
            candidate="${@: -1}"
            if [[ "$candidate" == *'static helper' ]]; then
              echo 'There is no dynamic section in this file.'
            else
              echo 'Dynamic section at offset 0x1 contains 1 entry:'
            fi
            """);
        await WriteExecutableAsync(
            Path.Combine(toolsDirectory, "ldd"),
            """
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$1" >> "$MAAUNIFIED_TEST_LDD_LOG"
            echo 'fixture dependency => /fixture/dependency'
            """);

        try
        {
            var result = await RunVerifierAsync(verifier, publishDirectory, toolsDirectory, lddLog);

            Assert.True(
                result.ExitCode == 0,
                $"Verifier exited with {result.ExitCode}. stdout={result.StandardOutput} stderr={result.StandardError}");
            var checkedFiles = await File.ReadAllLinesAsync(lddLog);
            Assert.Contains(checkedFiles, path => path.EndsWith("libe_sqlcipher.so", StringComparison.Ordinal));
            Assert.Contains(checkedFiles, path => path.EndsWith("lib MaaCore dependency.so", StringComparison.Ordinal));
            Assert.DoesNotContain(checkedFiles, path => path.EndsWith("not-an-elf.so", StringComparison.Ordinal));
            Assert.DoesNotContain(checkedFiles, path => path.EndsWith("static helper", StringComparison.Ordinal));
            Assert.DoesNotContain(checkedFiles, path => path.EndsWith("MAAUnified", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("libmissing.so => not found", 0, "unresolved native dependency")]
    [InlineData("ldd fixture failure", 42, "ldd failed")]
    public async Task LinuxArtifactVerifierRejectsDependencyFailuresOutsideSqlCipher(
        string lddFailureOutput,
        int lddExitCode,
        string expectedError)
    {
        if (OperatingSystem.IsWindows()) return;

        var sourceRoot = FindSourceRoot();
        var verifier = Path.Combine(sourceRoot, "CI", "verify-sensitive-storage-artifact.sh");
        var testRoot = Path.Combine(Path.GetTempPath(), "maa-unified-artifact-test", Guid.NewGuid().ToString("N"));
        var publishDirectory = Path.Combine(testRoot, "publish");
        var toolsDirectory = Path.Combine(testRoot, "tools");
        var executable = Path.Combine(publishDirectory, "bin", "MAAUnified");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        Directory.CreateDirectory(Path.Combine(publishDirectory, "native"));
        Directory.CreateDirectory(toolsDirectory);
        await WriteFakeElfAsync(Path.Combine(publishDirectory, "native", "libe_sqlcipher.so"));
        await WriteFakeElfAsync(Path.Combine(publishDirectory, "libMaaCore.so"));
        await WriteExecutableAsync(
            executable,
            """
            #!/usr/bin/env bash
            exit 99
            """);
        await WriteExecutableAsync(
            Path.Combine(toolsDirectory, "readelf"),
            """
            #!/usr/bin/env bash
            set -euo pipefail
            echo 'Dynamic section at offset 0x1 contains 1 entry:'
            """);
        await WriteExecutableAsync(
            Path.Combine(toolsDirectory, "ldd"),
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            if [[ "$1" == *libMaaCore.so ]]; then
              echo '{{lddFailureOutput}}'
              exit {{lddExitCode}}
            fi
            echo 'fixture dependency => /fixture/dependency'
            """);

        try
        {
            var result = await RunVerifierAsync(verifier, publishDirectory, toolsDirectory);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(expectedError, result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("libMaaCore.so", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("windows", "e_sqlcipher.dll", "SQLCipher native DLL")]
    [InlineData("macos", "libe_sqlcipher.dylib", "SQLCipher native dylib")]
    public async Task NonLinuxArtifactVerifierRejectsSqlCipherNativeLibrary(
        string platform,
        string nativeFileName,
        string expectedError)
    {
        if (OperatingSystem.IsWindows()) return;

        var sourceRoot = FindSourceRoot();
        var verifier = Path.Combine(sourceRoot, "CI", "verify-sensitive-storage-artifact.sh");
        var testRoot = Path.Combine(Path.GetTempPath(), "maa-unified-artifact-test", Guid.NewGuid().ToString("N"));
        var publishDirectory = Path.Combine(testRoot, "publish");
        var nativeDirectory = Path.Combine(publishDirectory, "runtimes", platform, "native");
        Directory.CreateDirectory(nativeDirectory);

        try
        {
            var cleanResult = await RunPlatformVerifierAsync(verifier, publishDirectory, platform);
            Assert.Equal(0, cleanResult.ExitCode);

            await File.WriteAllTextAsync(Path.Combine(nativeDirectory, nativeFileName), "fixture");
            var contaminatedResult = await RunPlatformVerifierAsync(verifier, publishDirectory, platform);

            Assert.NotEqual(0, contaminatedResult.ExitCode);
            Assert.Contains(expectedError, contaminatedResult.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void AppProjectReferencesSqlCipherOnlyForLinuxTargets()
    {
        var sourceRoot = FindSourceRoot();
        var project = XDocument.Load(Path.Combine(sourceRoot, "App", "MAAUnified.App.csproj"));
        var references = project
            .Descendants("ProjectReference")
            .Select(element => new
            {
                Include = ((string?)element.Attribute("Include") ?? string.Empty).Replace('\\', '/'),
                Condition = (string?)element.Attribute("Condition") ?? string.Empty,
            })
            .ToArray();

        var systemReference = Assert.Single(
            references,
            reference => reference.Include.EndsWith(
                "/SecureStorage.System/MAAUnified.SecureStorage.System.csproj",
                StringComparison.Ordinal));
        Assert.True(string.IsNullOrWhiteSpace(systemReference.Condition));

        var linuxReference = Assert.Single(
            references,
            reference => reference.Include.EndsWith(
                "/SecureStorage.Linux/MAAUnified.SecureStorage.Linux.csproj",
                StringComparison.Ordinal));
        Assert.Contains("IsOSPlatform('Linux')", linuxReference.Condition, StringComparison.Ordinal);
        Assert.Contains("linux-x64", linuxReference.Condition, StringComparison.Ordinal);
        Assert.Contains("linux-arm64", linuxReference.Condition, StringComparison.Ordinal);
        Assert.DoesNotContain("win-", linuxReference.Condition, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("osx-", linuxReference.Condition, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunVerifierAsync(
        string verifier,
        string publishDirectory,
        string toolsDirectory,
        string? lddLog = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { verifier, publishDirectory, "linux" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["PATH"] = $"{toolsDirectory}:{Environment.GetEnvironmentVariable("PATH")}";
        if (lddLog is not null)
        {
            startInfo.Environment["MAAUNIFIED_TEST_LDD_LOG"] = lddLog;
        }

        using var process = Process.Start(startInfo)!;
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, standardOutput, standardError);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunPlatformVerifierAsync(
        string verifier,
        string publishDirectory,
        string platform)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { verifier, publishDirectory, platform },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)!;
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, standardOutput, standardError);
    }

    private static async Task WriteExecutableAsync(string path, string contents)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        await File.WriteAllTextAsync(path, contents);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static Task WriteFakeElfAsync(string path)
        => File.WriteAllBytesAsync(path, [0x7f, (byte)'E', (byte)'L', (byte)'F', 0x01]);

    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CI", "verify-sensitive-storage-artifact.sh")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the MAAUnified source root.");
    }
}
