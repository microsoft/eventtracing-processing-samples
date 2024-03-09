// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using System;
using System.IO;

namespace FanNoiseSignal_Checker
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Usage: FanNoiseSignal_Checker.exe <AcpiFanNoiseImpact.etl>");
                return;
            }

            string filePath = args[0];

            if (!File.Exists(filePath))
            {
                Console.Error.WriteLine("File does not exist! Please check the file path again.");
                return;
            }

            try
            {
                ProcessFanNoiseSignal(filePath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"An error occurred: {ex.Message}");
            }

            Console.WriteLine("Press any key to exit...");
            _ = Console.ReadKey();
        }

        private static void ProcessFanNoiseSignal(string filePath)
        {
            string resultFilePath = Path.GetFullPath($"FanNoiseSignal_Result_{DateTime.Now:yyyyMMdd_HHmm}.txt");

            using (StreamWriter resultWriter = new StreamWriter(resultFilePath))
            {
                var trace = TraceProcessor.Create(filePath);
                Guid powerProviderGuid = new Guid("331C3B3A-2005-44C2-AC5E-77220C37D6B4"); // The GUID of Microsoft.Windows.Kernel.Power
                var pendingGenericEvent = trace.UseGenericEvents(powerProviderGuid);

                trace.Process();

                var traceMetadata = trace.UseMetadata();
                var traceStartTime = traceMetadata.StartTime;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Trace Start Time: {traceStartTime}");
                resultWriter.WriteLine($"Trace Start Time: {traceStartTime}");
                Console.ResetColor();

                var genericEventData = pendingGenericEvent.Result;
                bool fandata = false;

                foreach (var genericEvent in genericEventData.Events)
                {
                    if (genericEvent.TaskName == "PopFanUpdateSpeed_UpdatedNoiseLevel")
                    {
                        var timestamp = genericEvent.Timestamp.DateTimeOffset;
                        var oldFanNoiseLevel = genericEvent.Fields[1].AsInt32;
                        var newFanNoiseLevel = genericEvent.Fields[2].AsInt32;

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"Log Time: {timestamp}, OldFanNoiseLevel: {oldFanNoiseLevel}, NewFanNoiseLevel: {newFanNoiseLevel}");
                        resultWriter.WriteLine($"Log Time: {timestamp}, OldFanNoiseLevel: {oldFanNoiseLevel}, NewFanNoiseLevel: {newFanNoiseLevel}");
                        Console.ResetColor();
                        fandata = true;
                    }
                }

                if (!fandata)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("There is no Fan Noise Signal level change.");
                    resultWriter.WriteLine("There is no Fan Noise Signal level change.");
                    Console.ResetColor();
                }
            }
        }
    }
}