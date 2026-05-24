// © Microsoft Corporation. All rights reserved.

using System;
using System.IO;

namespace MemoryUsageChecker
{
    internal sealed class OutputWriter : IDisposable
    {
        private readonly StreamWriter _file;

        public OutputWriter(string filePath)
        {
            _file = new StreamWriter(filePath);
        }

        public int TopN { get; init; } = 10;

        public int TopK { get; init; } = 5;

        public void WriteHeader(string message)     => Write(message, ConsoleColor.Yellow);
        public void WriteSubHeader(string message)  => Write(message, ConsoleColor.Cyan);
        public void WriteData(string message)       => Write(message, ConsoleColor.Blue);
        public void WriteStackFrame(string message) => Write(message, ConsoleColor.DarkGray);
        public void WriteFinding(string message)    => Write(message, ConsoleColor.Green);
        public void WriteNotable(string message)    => Write(message, ConsoleColor.Magenta);
        public void WriteSkipped(string message)    => Write(message, ConsoleColor.DarkGray);
        public void WriteWarning(string message)    => Write(message, ConsoleColor.Red);
        public void WriteInfo(string message)       => Write(message, ConsoleColor.White);
        public void WriteBlank()                    => Write(string.Empty, ConsoleColor.White);

        public void Write(string message, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            _file.WriteLine(message);
            Console.ResetColor();
        }

        public void Dispose() => _file.Dispose();
    }
}
