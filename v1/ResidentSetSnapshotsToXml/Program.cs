// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace ResidentSetSnapshotsToXml
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length != 4)
            {
                Console.Error.WriteLine("Usage: ResidentSetSnapshotsToXml <trace.etl> <start (s)> <end (s)> <out.xml>");
                return;
            }

            string tracePath = args[0];
            Timestamp startTime = Timestamp.FromSeconds(decimal.Parse(args[1]));
            Timestamp stopTime = Timestamp.FromSeconds(decimal.Parse(args[2]));
            string xmlPath = args[3];

            IReadOnlyDictionary<PageKey, uint> pageCounts = GetResidentSetPageCounts(tracePath, startTime, stopTime);
            WritePageCountsToXml(pageCounts, xmlPath, startTime, stopTime);
        }

        private static IReadOnlyDictionary<PageKey, uint> GetResidentSetPageCounts(string tracePath,
            Timestamp startTime, Timestamp stopTime)
        {
            using (ITraceProcessor trace = TraceProcessor.Create(tracePath))
            {
                IPendingResult<IResidentSetDataSource> pendingResidentSet = trace.UseResidentSetData();

                trace.Process();

                IResidentSetDataSource residentSetData = pendingResidentSet.Result;

                Dictionary<PageKey, uint> pageCounts = new Dictionary<PageKey, uint>();

                foreach (IResidentSetSnapshot snapshot in residentSetData.Snapshots)
                {
                    if (snapshot.Timestamp < startTime || snapshot.Timestamp > stopTime)
                    {
                        continue;
                    }

                    foreach (IResidentSetPage page in snapshot.Pages)
                    {
                        PageKey key = new PageKey(snapshot.Timestamp, page.MemoryManagerListType, page.Priority);

                        if (!pageCounts.ContainsKey(key))
                        {
                            pageCounts.Add(key, 0);
                        }

                        ++pageCounts[key];
                    }
                }

                return pageCounts;
            }
        }

        private static void WritePageCountsToXml(IReadOnlyDictionary<PageKey, uint> pageCounts, string xmlFilename,
            Timestamp startTime, Timestamp stopTime)
        {
            using (XmlWriter writer = XmlWriter.Create(xmlFilename, new XmlWriterSettings() { Indent = true }))
            {
                writer.WriteStartDocument();

                writer.WriteStartElement("ExportedMetrics");

                writer.WriteStartElement("TimeRange");
                writer.WriteAttributeString("StartTime", $"{startTime.TotalMilliseconds}");
                writer.WriteAttributeString("EndTime", $"{stopTime.TotalMilliseconds}");

                writer.WriteStartElement("Table");
                writer.WriteAttributeString("TableName", "Resident Set Summary Table");
                writer.WriteAttributeString("PresetName", "Snapshot Sublist Breakdown by Category");

                int valueIndex = 0;

                foreach (PageKey key in pageCounts.Keys.OrderBy(k => k.Timestamp).ThenBy(k => k.ListType.ToString())
                    .ThenBy(k => k.Priority))
                {
                    writer.WriteStartElement("Values");
                    writer.WriteAttributeString("Index", $"{valueIndex++}");

                    writer.WriteStartElement("Value");
                    writer.WriteAttributeString("Name", "Snapshot Time (s)");
                    writer.WriteString($"{((double)key.Timestamp.Nanoseconds / 1000000000.0):0.#########}");
                    writer.WriteEndElement(); // Value

                    writer.WriteStartElement("Value");
                    writer.WriteAttributeString("Name", "MMList");
                    writer.WriteString($"{key.ListType}");
                    writer.WriteEndElement(); // Value

                    writer.WriteStartElement("Value");
                    writer.WriteAttributeString("Name", "Page Priority");
                    writer.WriteString($"{key.Priority}");
                    writer.WriteEndElement(); // Value

                    writer.WriteStartElement("Value");
                    writer.WriteAttributeString("Name", "Page Count");
                    writer.WriteString($"{pageCounts[key]}");
                    writer.WriteEndElement(); // Value

                    writer.WriteEndElement(); // Values
                }

                writer.WriteEndElement(); // Table
                writer.WriteEndElement(); // TimeRange
                writer.WriteEndElement(); // ExportedMetrics
                writer.WriteEndDocument();
            }
        }

        private struct PageKey : IEquatable<PageKey>
        {
            public PageKey(Timestamp timestamp, MemoryManagerListType listType, int priority)
            {
                Timestamp = timestamp;
                ListType = listType;
                Priority = priority;
            }

            public Timestamp Timestamp { get; }

            public MemoryManagerListType ListType { get; }

            public int Priority { get; }

            public override int GetHashCode()
            {
                unchecked
                {
                    return Timestamp.GetHashCode() ^ (int)ListType ^ Priority;
                }
            }

            public override bool Equals(object other)
            {
                if (object.ReferenceEquals(other, null))
                {
                    return false;
                }

                if (!(other is PageKey))
                {
                    return false;
                }

                return Equals((PageKey)other);
            }

            public bool Equals(PageKey other)
            {
                return Timestamp == other.Timestamp && ListType == other.ListType && Priority == other.Priority;
            }
        }
    }
}
