// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;

namespace FanNoiseSignal_Checker
{
    internal class Program
    {
        private static readonly Guid Microsoft_Windows_Kernel_Power = Guid.Parse("63bca7a1-77ec-4ea7-95d0-98d3f0c0ebf7");
        private static readonly string UpdatedNoiseLevel = @"PopFanUpdateSpeed_UpdatedNoiseLevel";
        private static readonly string TripPoint = @"PopFanUpdateSpeed_TripPoint";

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
                ITraceProcessorSettings tps = new TraceProcessorSettings { AllowLostEvents = true };
                var trace = TraceProcessor.Create(filePath, tps);

                var traceMetadata = trace.UseMetadata();
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Trace Start Time:\t{traceMetadata.StartTime}");
                resultWriter.WriteLine($"Trace Start Time:\t{traceMetadata.StartTime}");
                Console.ResetColor();
                Console.WriteLine();

                var pendingKernelPowerEvent = trace.UseGenericEvents(Microsoft_Windows_Kernel_Power);

                trace.Process();

                if (pendingKernelPowerEvent.HasResult == false)
                {
                    Console.Error.WriteLine(@"No Microsoft.Windows.Kernel.Power events found in the trace.");
                    return;
                }

                var genericEventData = pendingKernelPowerEvent.Result;
                bool fandata = false;

                foreach (var genericEvent in genericEventData.Events)
                {
                    if (genericEvent.TaskName == UpdatedNoiseLevel)
                    {
                        var timestamp = genericEvent.Timestamp.DateTimeOffset;
                        var oldFanNoiseLevel = genericEvent.Fields[1].AsInt32;
                        var newFanNoiseLevel = genericEvent.Fields[2].AsInt32;

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"Log Time:\t\t{timestamp}: OldFanNoiseLevel: {oldFanNoiseLevel}, NewFanNoiseLevel: {newFanNoiseLevel}");
                        resultWriter.WriteLine($"Log Time:\t\t{timestamp}: OldFanNoiseLevel: {oldFanNoiseLevel}, NewFanNoiseLevel: {newFanNoiseLevel}");
                        Console.ResetColor();
                        fandata = true;
                    }
                    else if (genericEvent.TaskName == TripPoint)
                    {
                        var timestamp = genericEvent.Timestamp.DateTimeOffset;
                        var lowTripPoint = genericEvent.Fields[1].AsUInt32;
                        var highTripPoint = genericEvent.Fields[2].AsUInt32;

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"Log Time:\t\t{timestamp}: LowTripPoint: {lowTripPoint}, HighTripPoint: {highTripPoint}");
                        resultWriter.WriteLine($"Log Time:\t\t{timestamp}: LowTripPoint: {lowTripPoint}, HighTripPoint: {highTripPoint}"); 
                        Console.ResetColor();
                        fandata = true;
                        Console.WriteLine();
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