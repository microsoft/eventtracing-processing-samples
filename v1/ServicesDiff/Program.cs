// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ServicesDiff
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: ServicesDiff.exe <left.etl> <right.etl>");
                return -1;
            }

            IReadOnlyList<string> left = GetServices(args[0]);
            IReadOnlyList<string> right = GetServices(args[1]);

            Console.WriteLine("Left only:");
            WriteLeftOnlyItems(left, right);
            Console.WriteLine();

            Console.WriteLine("Right only:");
            WriteLeftOnlyItems(right, left);
            Console.WriteLine();

            return 0;
        }

        private static IReadOnlyList<string> GetServices(string tracePath)
        {
            using (ITraceProcessor trace = TraceProcessor.Create(tracePath))
            {
                IPendingResult<IServiceDataSource> pendingServices = trace.UseServices();

                trace.Process();

                IServiceDataSource serviceData = pendingServices.Result;

                return serviceData.Services.Select(s => Cleanup(s.Name)).ToArray();
            }
        }

        private static void WriteLeftOnlyItems(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            foreach (string item in left.Except(right))
            {
                Console.WriteLine(item);
            }
        }

        // Some service names include "_<identifier>" at the end. This method removes that suffix so that the same
        // services will have equivalent service names for diff purposes.
        private static string Cleanup(string serviceName)
        {
            int i = serviceName.IndexOf('_');

            if (i == -1)
            {
                return serviceName;
            }

            return serviceName.Substring(0, i);
        }
    }
}
