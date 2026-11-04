using System.Diagnostics;
using System.IO;

namespace pharmacyApp.Modules.Medula;

internal static class PythonImageReader
{
    private const string ScriptFileName = "permittedSolution.py";

    public static async Task<string> ReadImageAsync(
        string imagePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var scriptPath = ResolveScriptPath();

        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(imagePath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("The permittedSolution.py process could not be started.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException($"{ScriptFileName} did not finish within {timeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{ScriptFileName} failed with exit code {process.ExitCode}: {error}");
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"{ScriptFileName} returned an empty result.");

        return output;
    }

    private static string ResolveScriptPath()
    {
        var path = Path.Combine(pharmacyApp.Infrastructure.AppPaths.RootDirectory, ScriptFileName);
        return File.Exists(path) ? path : throw new FileNotFoundException(
            $"{ScriptFileName} was not found in the local folder.");
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
