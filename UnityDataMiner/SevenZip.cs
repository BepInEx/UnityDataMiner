using System;
using System.Diagnostics;
using System.Threading.Tasks;
using NuGet.Packaging;

namespace UnityDataMiner;

public class SevenZip
{
    public static async Task ExtractAsync(string archivePath, string outputDirectory, params string[] fileFilter)
    {
        var processStartInfo = new ProcessStartInfo("7z")
        {
            ArgumentList =
            {
                "e", // extract
                "-y", // assume Yes on all queries
                archivePath,
                $"-o{outputDirectory}",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        processStartInfo.ArgumentList.AddRange(fileFilter);

        var process = Process.Start(processStartInfo) ?? throw new SevenZipException("Couldn't start 7z process");

        await process.WaitForExitAsync();

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
