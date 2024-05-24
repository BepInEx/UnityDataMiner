using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDataMiner;

public class EuUnstrip
{
    public static async Task EnsureInstalled(CancellationToken cancellationToken = default)
    {
        var process = Process.Start(new ProcessStartInfo("eu-unstrip", "--version")
        {
            RedirectStandardOutput = true,
        }) ?? throw new EuUnstripException("Couldn't start eu-unstrip process");
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new EuUnstripException("eu-unstrip is not installed");
        }
    }

    public static async Task UnstripAsync(string strippedPath, string symbolsPath, CancellationToken cancellationToken = default)
    {
        var processStartInfo = new ProcessStartInfo("eu-unstrip")
        {
            ArgumentList =
            {
                strippedPath,
                symbolsPath,
                $"--output={strippedPath}",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var process = Process.Start(processStartInfo) ?? throw new EuUnstripException("Couldn't start eu-unstrip process");

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new EuUnstripException("eu-unstrip returned " + process.ExitCode + "\n" + (await process.StandardError.ReadToEndAsync()).Trim());
        }

        File.Delete(symbolsPath);
    }
}

public class EuUnstripException : Exception
{
    public EuUnstripException(string? message) : base(message)
    {
    }
}
