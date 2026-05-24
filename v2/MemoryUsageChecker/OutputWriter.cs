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

        // Size-ranked findings. Use these when emitting a list sorted descending
        // by memory size, so the worst offender is most visually prominent.
        // Convention: rank 1 -> WriteCritical, top tier (~third) -> WriteHigh,
        // remaining top-N -> WriteNormal, truncated tail -> WriteTail.
        public void WriteCritical(string message)   => Write(message, ConsoleColor.Red);
        public void WriteHigh(string message)       => Write(message, ConsoleColor.Yellow);
        public void WriteNormal(string message)     => Write(message, ConsoleColor.White);
        public void WriteTail(string message)       => Write(message, ConsoleColor.DarkGray);

        /// <summary>
        /// Writes <paramref name="message"/> with a color picked by rank so that
        /// callers can iterate a size-sorted list and not hand-pick colors.
        /// Rank 1 is the largest. <paramref name="total"/> is the count of items
        /// being emitted (after Top-N truncation).
        /// </summary>
        public void WriteRanked(int rank, int total, string message)
        {
            if (rank <= 1)
            {
                WriteCritical(message);
            }
            else if (rank <= Math.Max(2, total / 3))
            {
                WriteHigh(message);
            }
            else
            {
                WriteNormal(message);
            }
        }

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
