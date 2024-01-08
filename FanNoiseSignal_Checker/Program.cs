namespace FanNoiseSignal_Checker
{
    using Microsoft.Windows.EventTracing;
    using System;
    using System.IO;

    internal class Program
    {
        private static void Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Usage: FanNoiseSignal_Checker.exe <AcpiFanNoiseImpact.etl>");
                return;
            }

            if (!string.Equals(Path.GetExtension(args[0]), ".etl", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Incorrect File! Please check the ETL file path again.");
                return;
            }

            try
            {
                ProcessFanNoiseSignal(Path.GetFullPath(args[0]));
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
            var resultFilePath = Path.GetFullPath(@"FanNoiseSignal_Result"+ "_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".txt");

            using (var resultWriter = new StreamWriter(resultFilePath))
            {
                var trace = TraceProcessor.Create(filePath);
                var pendingGenericEvent = trace.UseGenericEvents();

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
                    if (genericEvent.ProviderName == @"Microsoft.Windows.Kernel.Power" && genericEvent.TaskName == @"PopFanUpdateSpeed_UpdatedNoiseLevel")
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