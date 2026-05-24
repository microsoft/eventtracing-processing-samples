// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing.Metadata;
using Microsoft.Windows.EventTracing.Processes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MemoryUsageChecker
{
    /// <summary>
    /// Formats process and image identifiers with friendly names and version
    /// numbers pulled from the PE resources captured by the trace. All helpers
    /// degrade gracefully when version metadata is missing, so the analyzers
    /// can call them unconditionally without first checking for nulls or
    /// catching exceptions.
    /// </summary>
    /// <remarks>
    /// The trace v2 SDK exposes PE-version data via <see cref="IImage"/>
    /// properties (<c>FileDescription</c>, <c>FileVersion</c>,
    /// <c>ProductName</c>, <c>ProductVersion</c>, <c>OriginalFileName</c>,
    /// <c>CompanyName</c>). Each <see cref="IProcess"/> has a list of loaded
    /// images; the "main" image is the one whose file name matches the
    /// process's <see cref="IProcess.ImageName"/>.
    /// <para>
    /// OS / system metadata properties on <see cref="ITraceMetadata"/> and
    /// <see cref="ISystemMetadata"/> are not documented stably across SDK
    /// versions, so <see cref="FormatOsHeader"/> uses reflection and
    /// gracefully falls back when fields are missing.
    /// </para>
    /// </remarks>
    internal static class ImageFormatter
    {
        /// <summary>
        /// Process identifier row prefix with fixed-width padding suitable
        /// for ranked Top-N tables:
        /// <code>msedge.exe                       (pid  12345)  [Microsoft Edge v137.0.3271.62]</code>
        /// </summary>
        public static string FormatProcess(IProcess process)
        {
            if (process == null) return "(unknown process)";
            string name = string.IsNullOrEmpty(process.ImageName) ? "(unknown)" : process.ImageName;
            string head = $"{name,-32} (pid {process.Id,6})";
            string suffix = FormatImageSuffix(FindMainImage(process));
            return string.IsNullOrEmpty(suffix) ? head : $"{head}  {suffix}";
        }

        /// <summary>
        /// Short process identifier (no padding) for sub-header lines:
        /// <code>msedge.exe (pid 12345) [Microsoft Edge v137.0.3271.62]</code>
        /// </summary>
        public static string FormatProcessShort(IProcess process)
        {
            if (process == null) return "(unknown process)";
            string name = string.IsNullOrEmpty(process.ImageName) ? "(unknown)" : process.ImageName;
            string head = $"{name} (pid {process.Id})";
            string suffix = FormatImageSuffix(FindMainImage(process));
            return string.IsNullOrEmpty(suffix) ? head : $"{head} {suffix}";
        }

        /// <summary>
        /// Returns just the <c>[Friendly Name vN.N.N.N]</c> suffix for an
        /// image; empty string when no PE-resource metadata is available.
        /// Prefers <c>FileDescription</c> over <c>ProductName</c> and
        /// <c>FileVersion</c> over <c>ProductVersion</c>.
        /// </summary>
        public static string FormatImageSuffix(IImage image)
        {
            if (image == null) return string.Empty;
            string desc = Clean(SafeGet(() => image.FileDescription));
            if (string.IsNullOrEmpty(desc)) desc = Clean(SafeGet(() => image.ProductName));
            string ver = Clean(SafeGet(() => image.FileVersion));
            if (string.IsNullOrEmpty(ver)) ver = Clean(SafeGet(() => image.ProductVersion));

            if (string.IsNullOrEmpty(desc) && string.IsNullOrEmpty(ver)) return string.Empty;
            if (!string.IsNullOrEmpty(desc) && !string.IsNullOrEmpty(ver)) return $"[{desc} v{ver}]";
            if (!string.IsNullOrEmpty(desc)) return $"[{desc}]";
            return $"[v{ver}]";
        }

        /// <summary>
        /// Driver identifier row used by the pool-allocation and
        /// driver-code-footprint analyses:
        /// <code>dxgkrnl.sys                   C:\WINDOWS\system32\drivers\dxgkrnl.sys  [DirectX Graphics Kernel Driver v10.0.26100.2454]</code>
        /// Falls back to <paramref name="fallbackLeaf"/> / <paramref name="fallbackPath"/>
        /// when <paramref name="image"/> is null or its properties are unavailable.
        /// </summary>
        public static string FormatDriverRow(IImage image, string fallbackLeaf, string fallbackPath)
        {
            string leaf = SafeGet(() => image?.FileName) ?? fallbackLeaf ?? "(unknown)";
            string path = SafeGet(() => image?.Path) ?? fallbackPath ?? string.Empty;
            string suffix = FormatImageSuffix(image);
            string head = string.IsNullOrEmpty(path) ? $"{leaf,-28}" : $"{leaf,-28}  {path}";
            return string.IsNullOrEmpty(suffix) ? head : $"{head}  {suffix}";
        }

        /// <summary>
        /// Compact driver identifier for sub-header / per-driver group titles
        /// where the full path would be too wide.
        /// </summary>
        public static string FormatDriverShort(IImage image, string fallbackLeaf)
        {
            string leaf = SafeGet(() => image?.FileName) ?? fallbackLeaf ?? "(unknown)";
            string suffix = FormatImageSuffix(image);
            return string.IsNullOrEmpty(suffix) ? leaf : $"{leaf} {suffix}";
        }

        /// <summary>
        /// Builds the JSON identity block for a process: image name, pid,
        /// friendly name (from <c>FileDescription</c>) and version string
        /// (from <c>FileVersion</c>). Designed so two runs can be matched on
        /// <c>ImageName</c> + <c>Version</c> (PID is run-local). Returns
        /// <c>null</c> when the process itself is null.
        /// </summary>
        public static JsonReport.ProcessIdentity BuildProcessIdentity(IProcess process)
        {
            if (process == null) return null;
            IImage image = FindMainImage(process);
            string desc = Clean(SafeGet(() => image?.FileDescription));
            if (string.IsNullOrEmpty(desc)) desc = Clean(SafeGet(() => image?.ProductName));
            string ver = Clean(SafeGet(() => image?.FileVersion));
            if (string.IsNullOrEmpty(ver)) ver = Clean(SafeGet(() => image?.ProductVersion));
            return new JsonReport.ProcessIdentity
            {
                ImageName = string.IsNullOrEmpty(process.ImageName) ? "(unknown)" : process.ImageName,
                Pid = (int)process.Id,
                FriendlyName = string.IsNullOrEmpty(desc) ? null : desc,
                Version = string.IsNullOrEmpty(ver) ? null : ver
            };
        }

        /// <summary>
        /// Builds the JSON identity block for a driver / image: file name,
        /// path, friendly name and version. Falls back to the supplied leaf
        /// / path when the SDK image object is null or doesn't expose those
        /// properties.
        /// </summary>
        public static JsonReport.DriverIdentity BuildDriverIdentity(IImage image, string fallbackLeaf, string fallbackPath)
        {
            string leaf = SafeGet(() => image?.FileName) ?? fallbackLeaf ?? "(unknown)";
            string path = SafeGet(() => image?.Path) ?? fallbackPath;
            string desc = Clean(SafeGet(() => image?.FileDescription));
            if (string.IsNullOrEmpty(desc)) desc = Clean(SafeGet(() => image?.ProductName));
            string ver = Clean(SafeGet(() => image?.FileVersion));
            if (string.IsNullOrEmpty(ver)) ver = Clean(SafeGet(() => image?.ProductVersion));
            return new JsonReport.DriverIdentity
            {
                FileName = leaf,
                Path = string.IsNullOrEmpty(path) ? null : path,
                FriendlyName = string.IsNullOrEmpty(desc) ? null : desc,
                Version = string.IsNullOrEmpty(ver) ? null : ver
            };
        }

        /// <summary>
        /// Picks the loaded image whose file name matches the process's
        /// primary image name (e.g. <c>msedge.exe</c> inside the
        /// <c>msedge.exe</c> process). Returns <c>null</c> if no match is
        /// found — for example, kernel pseudo-processes such as
        /// <c>System</c> may have no matching image.
        /// </summary>
        public static IImage FindMainImage(IProcess process)
        {
            if (process == null) return null;
            IReadOnlyList<IImage> images;
            try { images = process.Images; } catch { return null; }
            if (images == null || images.Count == 0) return null;
            if (string.IsNullOrEmpty(process.ImageName)) return images.FirstOrDefault();
            foreach (var img in images)
            {
                string fileName = SafeGet(() => img.FileName);
                if (!string.IsNullOrEmpty(fileName) &&
                    string.Equals(fileName, process.ImageName, StringComparison.OrdinalIgnoreCase))
                {
                    return img;
                }
            }
            return images.FirstOrDefault();
        }

        /// <summary>
        /// Composes an OS / system summary line from whatever the SDK
        /// exposes on the given metadata interfaces. Uses reflection so the
        /// sample remains compatible if SDK property names shift between
        /// versions. Returns <c>"(OS info not available in trace metadata)"</c>
        /// when nothing could be discovered.
        /// </summary>
        public static string FormatOsHeader(ITraceMetadata trace, ISystemMetadata system)
        {
            var parts = new List<string>();
            // Friendly OS name
            string osName = FirstNonEmpty(trace, "OSName")
                         ?? FirstNonEmpty(system, "OSName")
                         ?? FirstNonEmpty(trace, "OperatingSystemName")
                         ?? FirstNonEmpty(system, "OperatingSystemName");
            if (!string.IsNullOrEmpty(osName)) parts.Add(osName);

            // Numeric version (Major.Minor.Build.UBR)
            string osVer = FirstNonEmpty(trace, "OSVersion")
                        ?? FirstNonEmpty(system, "OSVersion");
            if (string.IsNullOrEmpty(osVer))
            {
                string composed = ComposeNumericVersion(trace) ?? ComposeNumericVersion(system);
                if (!string.IsNullOrEmpty(composed)) osVer = composed;
            }
            if (!string.IsNullOrEmpty(osVer)) parts.Add($"v{osVer}");

            // Build lab string (e.g. "26100.1.amd64fre.ge_release.240331-1435")
            string buildLab = FirstNonEmpty(trace, "OSBuildLab")
                           ?? FirstNonEmpty(system, "OSBuildLab")
                           ?? FirstNonEmpty(trace, "BuildLab")
                           ?? FirstNonEmpty(system, "BuildLab");
            if (!string.IsNullOrEmpty(buildLab)) parts.Add($"BuildLab={buildLab}");

            // Architecture (Amd64, Arm64, ...)
            string arch = FirstNonEmpty(trace, "Architecture")
                       ?? FirstNonEmpty(system, "Architecture")
                       ?? FirstNonEmpty(trace, "ProcessorArchitecture")
                       ?? FirstNonEmpty(system, "ProcessorArchitecture");
            if (!string.IsNullOrEmpty(arch)) parts.Add(arch);

            // Machine / host name
            string machine = FirstNonEmpty(trace, "MachineName")
                          ?? FirstNonEmpty(system, "MachineName")
                          ?? FirstNonEmpty(trace, "ComputerName")
                          ?? FirstNonEmpty(system, "ComputerName");
            if (!string.IsNullOrEmpty(machine)) parts.Add($"Machine={machine}");

            return parts.Count == 0 ? "(OS info not available in trace metadata)" : string.Join(" / ", parts);
        }

        /// <summary>
        /// Public wrapper around the reflection-based property reader used by
        /// <see cref="FormatOsHeader"/>. Lets <c>Program.cs</c> pick out
        /// individual OS fields (<c>OSVersion</c>, <c>OSBuildLab</c>,
        /// <c>Architecture</c>, <c>MachineName</c>) for the JSON sidecar
        /// without duplicating the SDK-version-tolerant reflection logic.
        /// Returns <c>null</c> when the property is missing, the value is
        /// null, the value is empty, or the read throws.
        /// </summary>
        public static string SafeReadProperty(object obj, string propName) => FirstNonEmpty(obj, propName);

        // ---- internals --------------------------------------------------

        /// <summary>Trims whitespace; returns <c>null</c> if the result is empty.</summary>
        private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        /// <summary>
        /// Invokes <paramref name="getter"/> and returns its result, or
        /// <c>null</c> if it throws. Lets the formatters be defensive about
        /// trace data without sprinkling try/catch through every call site.
        /// </summary>
        private static string SafeGet(Func<string> getter)
        {
            try { return getter(); } catch { return null; }
        }

        /// <summary>
        /// Reflectively reads a public instance string-convertible property
        /// from <paramref name="obj"/> by name, returning <c>null</c> if the
        /// property is missing, the value is null, or the value is empty.
        /// </summary>
        private static string FirstNonEmpty(object obj, string propName)
        {
            if (obj == null || string.IsNullOrEmpty(propName)) return null;
            try
            {
                PropertyInfo p = obj.GetType().GetProperty(propName,
                    BindingFlags.Instance | BindingFlags.Public);
                if (p == null) return null;
                object v = p.GetValue(obj);
                if (v == null) return null;
                string s = v.ToString();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Builds a Windows-style <c>Major.Minor.Build.UBR</c> version string
        /// when explicit numeric properties are exposed but no composite
        /// string property is available. Missing pieces render as <c>?</c>.
        /// </summary>
        private static string ComposeNumericVersion(object obj)
        {
            if (obj == null) return null;
            string maj = FirstNonEmpty(obj, "MajorVersion") ?? FirstNonEmpty(obj, "OSMajorVersion");
            string min = FirstNonEmpty(obj, "MinorVersion") ?? FirstNonEmpty(obj, "OSMinorVersion");
            string bld = FirstNonEmpty(obj, "BuildNumber") ?? FirstNonEmpty(obj, "OSBuildNumber");
            string rev = FirstNonEmpty(obj, "UpdateBuildRevision")
                      ?? FirstNonEmpty(obj, "BuildRevision")
                      ?? FirstNonEmpty(obj, "OSUpdateBuildRevision");
            if (string.IsNullOrEmpty(maj) && string.IsNullOrEmpty(min) &&
                string.IsNullOrEmpty(bld) && string.IsNullOrEmpty(rev))
            {
                return null;
            }
            return $"{maj ?? "?"}.{min ?? "?"}.{bld ?? "?"}.{rev ?? "?"}";
        }
    }
}
