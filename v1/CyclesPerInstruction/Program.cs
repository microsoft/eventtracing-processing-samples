// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using System;
using System.Collections.Generic;
using System.Linq;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: CyclesPerInstruction.exe <trace.etl>");
            return 1;
        }

        string tracePath = args[0];

        TraceProcessorSettings settings = new TraceProcessorSettings { AllowLostEvents = true };

        using (ITraceProcessor trace = TraceProcessor.Create(tracePath, settings))
        {
            IPendingResult<IProcessorCounterDataSource> pendingCounterData = trace.UseProcessorCounters();

            trace.Process();

            IProcessorCounterDataSource counterData = pendingCounterData.Result;

            if (!counterData.HasCycleCount)
            {
                Console.Error.WriteLine("Trace does not contain cycle count data.");
                return 2;
            }

            if (!counterData.HasInstructionCount)
            {
                Console.Error.WriteLine("Trace does not contain instruction count data.");
                return 2;
            }

            Dictionary<string, ulong> cyclesByProcess = new Dictionary<string, ulong>();
            Dictionary<string, ulong> instructionsByProcess = new Dictionary<string, ulong>();

            foreach (IProcessorCounterContextSwitchDelta delta in counterData.ContextSwitchCounterDeltas)
            {
                string processName = delta.Thread?.Process?.ImageName ?? "Unknown";

                if (!cyclesByProcess.ContainsKey(processName))
                {
                    cyclesByProcess.Add(processName, 0);
                    instructionsByProcess.Add(processName, 0);
                }

                cyclesByProcess[processName] += delta.CycleCount.Value;
                instructionsByProcess[processName] += delta.InstructionCount.Value;
            }

            foreach (string processName in cyclesByProcess.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                ulong cycles = cyclesByProcess[processName];
                ulong instructions = instructionsByProcess[processName];
                decimal cyclesPerInstruction = ((decimal)cycles) / instructions;
                Console.WriteLine($"{processName}: Cycles: {cycles}; Instructions: {instructions}; " +
                    $"CPI: {cyclesPerInstruction:0.####}");
            }

            return 0;
        }
    }
}
