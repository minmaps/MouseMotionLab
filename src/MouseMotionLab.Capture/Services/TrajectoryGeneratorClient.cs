using System.Diagnostics;
using System.IO;
using MouseMotionLab.Core.Contracts;

namespace MouseMotionLab.Capture.Services;

public sealed class TrajectoryGeneratorClient
{
    private readonly string _repositoryRoot;
    private readonly string _pythonExecutable;

    public TrajectoryGeneratorClient(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        var virtualEnvironmentPython = Path.Combine(_repositoryRoot, ".venv", "Scripts", "python.exe");
        _pythonExecutable = File.Exists(virtualEnvironmentPython) ? virtualEnvironmentPython : "python";
    }

    public async Task<TrajectoryResult> GenerateAsync(
        TrajectoryRequest request,
        string? artifactPath,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var mlRoot = Path.Combine(_repositoryRoot, "ml");
        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonExecutable,
            WorkingDirectory = mlRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["PYTHONPATH"] = Path.Combine(mlRoot, "src");
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("mousemotion.cli");
        startInfo.ArgumentList.Add("generate");
        startInfo.ArgumentList.Add("--dx");
        startInfo.ArgumentList.Add(request.DxCounts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--dy");
        startInfo.ArgumentList.Add(request.DyCounts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--dpi");
        startInfo.ArgumentList.Add(request.Dpi.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--output-hz");
        startInfo.ArgumentList.Add(request.OutputHz.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--seed");
        startInfo.ArgumentList.Add(request.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add(request.Mode);
        startInfo.ArgumentList.Add("--json-stdout");

        if (!string.IsNullOrWhiteSpace(artifactPath))
        {
            var absoluteArtifact = Path.IsPathRooted(artifactPath)
                ? artifactPath
                : Path.Combine(_repositoryRoot, artifactPath);
            startInfo.ArgumentList.Add("--artifact");
            startInfo.ArgumentList.Add(Path.GetFullPath(absoluteArtifact));
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start the Python trajectory generator.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Trajectory generator exited with code {process.ExitCode}: {error.Trim()}");
        }

        var result = TrajectoryResult.FromJson(output);
        result.ValidateAgainst(request);
        return result;
    }
}
