using AssetRipper.Primitives;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDataMiner
{
    internal class DownloadableAssetCollection
    {
        // url -> path
        private readonly Dictionary<string, string> assets = new();

        // returns actual asset path
        public string AddAsset(string url, string destPath)
        {
            if (!assets.TryGetValue(url, out var realPath))
            {
                assets.Add(url, realPath = destPath);
            }

            return realPath;
        }

        public async Task DownloadAssetsAsync(Func<string, string, CancellationToken, Task> downloadFunc, UnityVersion version,
            SemaphoreSlim? downloadLock = null, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                try
                {
                    if (downloadLock is not null)
                    {
                        await downloadLock.WaitAsync(cancellationToken);
                    }
                    try
                    {
                        foreach (var (url, dest) in assets)
                        {
                            await downloadFunc(url, dest, cancellationToken);
                        }
                    }
                    finally
                    {
                        if (!cancellationToken.IsCancellationRequested && downloadLock is not null)
                        {
                            downloadLock.Release();
                        }
                    }

                    break;
                }
                catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
                {
                    Log.Warning("Failed to download {Version}, waiting 5 seconds before retrying...", version);
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }
    }
}
