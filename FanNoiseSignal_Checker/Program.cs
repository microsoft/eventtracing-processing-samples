// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;

namespace FanNoiseSignal_Checker
{
    internal class Program
    {
        // Microsoft-Windows-Kernel-Acpi provider GUID
        private static readonly Guid Microsoft_Windows_Kernel_Acpi = Guid.Parse("c514638f-7723-485b-bcfc-96565d735d4a"); 
        private static readonly string FanStatusChange = @"FanStatusChange";

        // Microsoft.Windows.Kernel.Power provider GUID
        private static readonly Guid Microsoft_Windows_Kernel_Power = Guid.Parse("63bca7a1-77ec-4ea7-95d0-98d3f0c0ebf7"); 
        private static readonly string UpdatedNoiseLevel = @"PopFanUpdateSpeed_UpdatedNoiseLevel";
        private static readonly string TripPoint = @"PopFanUpdateSpeed_TripPoint";
        private static readonly string NoiseImpactSupport = @"PopFanUpdateSpeed_Done";

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
                WriteResult(resultWriter, $"Trace Start Time:\t{traceMetadata.StartTime}", ConsoleColor.Yellow);
                WriteResult(resultWriter, "");

                var pendingGenericEvent = trace.UseGenericEvents(new Guid[] { Microsoft_Windows_Kernel_Acpi, Microsoft_Windows_Kernel_Power });

                trace.Process();

                if (pendingGenericEvent.HasResult == false)
                {
                    WriteResult(resultWriter, @"No Microsoft.Windows.Kernel.Power events or Microsoft-Windows-Kernel-Acpi events found in the trace.", ConsoleColor.Red);
                    return;
                }

                var genericEventData = pendingGenericEvent.Result;
                bool fanNoiseSignalLevelChanged = false;
                bool noiseImpactSupport = false;

                foreach (var genericEvent in genericEventData.Events)
                {
                    if (genericEvent.TaskName == FanStatusChange)
                    {
                        var timestamp = genericEvent.Timestamp.DateTimeOffset;
                        var fanBiosName = genericEvent.Fields[1].AsString;
                        var control = genericEvent.Fields[2].AsUInt32;
                        var speed = genericEvent.Fields[3].AsUInt32;

                        WriteResult(resultWriter, $"Log Time:\t\t{timestamp}: FanBiosName: {fanBiosName}, Control: {control}, Speed: {speed}", ConsoleColor.Blue);
                    }
                    else if (genericEvent.TaskName == UpdatedNoiseLevel)
                    {
                        var timestamp = genericEvent.Timestamp.DateTimeOffset;
                        var oldFanNoiseLevel = genericEvent.Fields[1].AsInt32;
                        var newFanNoiseLevel = genericEvent.Fields[2].AsInt32;

                        WriteResult(resultWriter, $"Log Time:\t\t{timestamp}: OldFanNoiseLevel: {oldFanNoiseLevel}, NewFanNoiseLevel: {newFanNoiseLevel}", ConsoleColor.Green);
                        fanNoiseSignalLevelChanged = true;
                    }
                    else if (genericEvent.TaskName == TripPoint)
                    {
                        var timestamp = genericEvent.Timestamp.DateTimeOffset;
                        var lowTripPoint = genericEvent.Fields[1].AsUInt32;
                        var highTripPoint = genericEvent.Fields[2].AsUInt32;

                        WriteResult(resultWriter, $"Log Time:\t\t{timestamp}: LowTripPoint: {lowTripPoint} (0x{lowTripPoint:X}), HighTripPoint: {highTripPoint} (0x{highTripPoint:X})", ConsoleColor.Cyan);
                        WriteResult(resultWriter, "");
                    }
                    else if (genericEvent.TaskName == NoiseImpactSupport)
                    {
                        noiseImpactSupport = genericEvent.Fields[2].AsBoolean;
                    }
                }

                if (!fanNoiseSignalLevelChanged)
                {
                    WriteResult(resultWriter, "There is no Fan Noise Signal level change.", ConsoleColor.Red);
                }

                if (!noiseImpactSupport)
                {
                    WriteResult(resultWriter, "There is no Noise Impact Support.", ConsoleColor.Red);
                }
            }
        }

        private static void WriteResult(StreamWriter writer, string message, ConsoleColor color = ConsoleColor.White)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            writer.WriteLine(message);
            Console.ResetColor();
        }
    }
}