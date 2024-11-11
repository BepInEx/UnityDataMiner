using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Packaging;

namespace UnityDataMiner;

public class SevenZip
{
    public static async Task EnsureInstalled(CancellationToken cancellationToken = default)
    {
        var process = Process.Start(new ProcessStartInfo("7zz", "--help")
        {
            RedirectStandardOutput = true,
        }) ?? throw new SevenZipException("Couldn't start 7z process");
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new EuUnstripException("7z is not installed");
        }
    }

    public static async Task ExtractAsync(string archivePath, string outputDirectory, IEnumerable<string>? fileFilter = null, bool flat = true, CancellationToken cancellationToken = default)
    {
        var processStartInfo = new ProcessStartInfo("7zz")
        {
            ArgumentList =
            {
                flat ? "e" : "x",
                "-ssc-", // case-insensitive mode
                "-y",
                archivePath,
                $"-o{outputDirectory}",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (fileFilter != null)
        {
            processStartInfo.ArgumentList.AddRange(fileFilter);
        }

        var process = Process.Start(processStartInfo) ?? throw new SevenZipException("Couldn't start 7z process");

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new SevenZipException("7z returned " + process.ExitCode + "\n" + (await process.StandardError.ReadToEndAsync()).Trim());
        }
    }
}

public class SevenZipException : Exception
{
    public SevenZipException(string? message) : base(message)
    {
    }
}
