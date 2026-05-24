// © Microsoft Corporation. All rights reserved.

using System;

namespace MemoryUsageChecker
{
    internal class Program
    {
        private static int Main(string[] args)
        {
            Console.Error.WriteLine("Usage: MemoryUsageChecker.exe <trace.etl> [--top N] [--symbols <path>] [--no-symbols]");
            return 1;
        }
    }
}
