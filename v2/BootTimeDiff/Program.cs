// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using System;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: BootTimeDiff.exe <left.etl> <right.etl>");
            return;
        }

        string leftTracePath = args[0];
        string rightTracePath = args[1];

        Timestamp leftBootTime = GetBootTime(leftTracePath);
        Timestamp rightBootTime = GetBootTime(rightTracePath);

        Console.WriteLine($"Boot Time Delta: {rightBootTime - leftBootTime} ({leftBootTime} vs {rightBootTime})");
    }

    static Timestamp GetBootTime(string tracePath)
    {
        Timestamp result = Timestamp.Zero;

        using (ITraceProcessor trace = TraceProcessor.Create(tracePath))
        {
            // Microsoft-Windows-Shell-Core
            trace.UseStreaming().UseUnparsedEvents(e =>
            {
                // PerfTrack_Explorer_ExplorerStartToDesktopReady
                if (e.Id != 27231) return;

                result = e.Timestamp;
            },
            new Guid("30336ed4-e327-447c-9de0-51b652c86108"));

            trace.Process();
        }

        return result;
    }
}
