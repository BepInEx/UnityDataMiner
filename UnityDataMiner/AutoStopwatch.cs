using System;
using System.Diagnostics;

namespace UnityDataMiner;

public class AutoStopwatch : Stopwatch, IDisposable
{
    public AutoStopwatch()
    {
        Start();
    }

    public void Dispose()
    {
        Stop();
    }
}
