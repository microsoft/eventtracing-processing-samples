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

            // Pull from ISystemMetadata.BuildInfo first — it's the only place
            // the SDK surfaces the full Update Build Revision ("UBR", e.g.
            // 8491 in 26220.8491) plus a friendly product name. The kernel
            // OSVersion property only carries Major.Minor.Build.
            object buildInfo = SafeGetMember(system, "BuildInfo");
            int? buildNumber   = SafeReadNullableInt(buildInfo, "BuildNumber");
            int? buildRevision = SafeReadNullableInt(buildInfo, "BuildRevision");
            string productName = FirstNonEmpty(buildInfo, "ProductName");
            string archFromBuildInfo = FirstNonEmpty(buildInfo, "Architecture");

            // 1. Product name (e.g. "Windows 11 Enterprise Insider Preview")
            if (!string.IsNullOrEmpty(productName))
            {
                parts.Add(productName);
            }
            else
            {
                string osName = FirstNonEmpty(trace, "OSName")
                             ?? FirstNonEmpty(system, "OSName")
                             ?? FirstNonEmpty(trace, "OperatingSystemName")
                             ?? FirstNonEmpty(system, "OperatingSystemName");
                if (!string.IsNullOrEmpty(osName)) parts.Add(osName);
            }

            // 2. Build line — prefer "Version 25H2 (OS Build 26220.8491)" to
            // mirror what winver shows. Falls back to plain "OS Build NNNNN"
            // when the marketing label is unknown, and finally to
            // "v<Major.Minor.Build>" when BuildInfo isn't populated.
            if (buildNumber.HasValue)
            {
                string buildText = buildRevision.HasValue
                    ? $"{buildNumber.Value}.{buildRevision.Value}"
                    : $"{buildNumber.Value}";
                string displayVersion = TryGetWindowsDisplayVersion(buildNumber.Value);
                parts.Add(string.IsNullOrEmpty(displayVersion)
                    ? $"OS Build {buildText}"
                    : $"Version {displayVersion} (OS Build {buildText})");
            }
            else
            {
                string osVer = FirstNonEmpty(trace, "OSVersion")
                            ?? FirstNonEmpty(system, "OSVersion");
                if (string.IsNullOrEmpty(osVer))
                {
                    string composed = ComposeNumericVersion(trace) ?? ComposeNumericVersion(system);
                    if (!string.IsNullOrEmpty(composed)) osVer = composed;
                }
                if (!string.IsNullOrEmpty(osVer)) parts.Add($"v{osVer}");
            }

            // 3. Architecture (Amd64, Arm64, ...)
            string arch = archFromBuildInfo
                       ?? FirstNonEmpty(trace, "Architecture")
                       ?? FirstNonEmpty(system, "Architecture")
                       ?? FirstNonEmpty(trace, "ProcessorArchitecture")
                       ?? FirstNonEmpty(system, "ProcessorArchitecture");
            if (!string.IsNullOrEmpty(arch)) parts.Add(arch);

            // 4. Machine / host name
            string machine = FirstNonEmpty(trace, "MachineName")
                          ?? FirstNonEmpty(system, "MachineName")
                          ?? FirstNonEmpty(trace, "ComputerName")
                          ?? FirstNonEmpty(system, "ComputerName")
                          ?? FirstNonEmpty(system, "Name");
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

        /// <summary>
        /// Returns the full Major.Minor.Build.UBR string (e.g. <c>"10.0.26220.8491"</c>)
        /// for the JSON sidecar. Reads <c>BuildNumber</c> + <c>BuildRevision</c>
        /// from <c>ISystemMetadata.BuildInfo</c> for the build/UBR and uses
        /// the kernel <c>OSVersion</c> for major/minor. Falls back to whatever
        /// <c>OSVersion</c> exposes when <c>BuildInfo</c> isn't populated.
        /// Returns <c>null</c> when neither source has data.
        /// </summary>
        public static string GetOsVersionWithRevision(ITraceMetadata trace, ISystemMetadata system)
        {
            object bi = SafeGetMember(system, "BuildInfo");
            int? build = SafeReadNullableInt(bi, "BuildNumber");
            int? ubr   = SafeReadNullableInt(bi, "BuildRevision");
            Version osVer = SafeGetMember(system, "OSVersion") as Version
                         ?? SafeGetMember(trace,  "OSVersion") as Version;
            if (build.HasValue)
            {
                int major = osVer?.Major ?? 10;
                int minor = osVer?.Minor ?? 0;
                return ubr.HasValue
                    ? $"{major}.{minor}.{build.Value}.{ubr.Value}"
                    : $"{major}.{minor}.{build.Value}";
            }
            if (osVer != null)
            {
                return osVer.Revision >= 0
                    ? $"{osVer.Major}.{osVer.Minor}.{osVer.Build}.{osVer.Revision}"
                    : $"{osVer.Major}.{osVer.Minor}.{osVer.Build}";
            }
            string osStr = FirstNonEmpty(trace, "OSVersion") ?? FirstNonEmpty(system, "OSVersion");
            return osStr;
        }

        /// <summary>
        /// Returns the OS product name from <c>BuildInfo.ProductName</c>
        /// (e.g. <c>"Windows 11 Enterprise Insider Preview"</c>), or
        /// <c>null</c> when unavailable.
        /// </summary>
        public static string GetOsProductName(ISystemMetadata system)
            => FirstNonEmpty(SafeGetMember(system, "BuildInfo"), "ProductName");

        /// <summary>
        /// Returns the Windows marketing release label for the trace's
        /// <c>BuildInfo.BuildNumber</c> (e.g. <c>"25H2"</c>), or <c>null</c>
        /// if the build isn't in the lookup table. This label is not stored
        /// in the ETL — see <see cref="TryGetWindowsDisplayVersion"/>.
        /// </summary>
        public static string GetOsDisplayVersion(ISystemMetadata system)
        {
            int? build = SafeReadNullableInt(SafeGetMember(system, "BuildInfo"), "BuildNumber");
            return build.HasValue ? TryGetWindowsDisplayVersion(build.Value) : null;
        }

        /// <summary>
        /// Returns the build-lab string (e.g.
        /// <c>"26220.5000.amd64fre.ge_release.250709-1212"</c>) from
        /// <c>BuildInfo.BuildLab</c>, with reflection fallbacks for older
        /// SDK shapes that exposed <c>OSBuildLab</c> directly.
        /// </summary>
        public static string GetOsBuildLab(ITraceMetadata trace, ISystemMetadata system)
            => FirstNonEmpty(SafeGetMember(system, "BuildInfo"), "BuildLab")
            ?? FirstNonEmpty(trace,  "OSBuildLab")
            ?? FirstNonEmpty(system, "OSBuildLab");

        /// <summary>
        /// Best-effort map from a Windows client mainline build number to its
        /// marketing release label (e.g. 26200 → "25H2"). The label is NOT
        /// stored in the ETL — Windows derives it from
        /// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\DisplayVersion</c>
        /// on the running OS — so this table has to be updated when a new
        /// mainline build ships. When the build isn't recognized, we return
        /// <c>null</c> and <see cref="FormatOsHeader"/> falls back to plain
        /// <c>"OS Build NNNNN.UBR"</c> rather than guessing.
        ///
        /// Sources of truth (re-sync this table when either page changes):
        ///   Windows 11: https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information
        ///   Windows 10: https://learn.microsoft.com/en-us/windows/release-health/release-information
        /// </summary>
        private static string TryGetWindowsDisplayVersion(int buildNumber)
        {
            return buildNumber switch
            {
                // Windows 11 — GA / LTSC builds per the Microsoft Learn release-info page.
                28000 => "26H1",   // GA 2026-02-10
                26200 => "25H2",   // GA 2025-09-30
                26100 => "24H2",   // GA 2024-10-01 (also Win 11 24H2 LTSC)
                22631 => "23H2",   // GA 2023-10-31
                22621 => "22H2",   // GA 2022-09-20
                22000 => "21H2",   // GA 2021-10-05 (Windows 11 1.0)

                // Windows 11 — Insider builds that flighted as the named release
                // train (not listed on the Learn page; included so winver-style
                // labels still resolve for Dev/Beta channel ETLs).
                26220 => "25H2",   // Insider Dev/Beta train for 25H2

                // Windows 10 — GA builds per the Microsoft Learn release-info page.
                19045 => "22H2",
                19044 => "21H2",
                19043 => "21H1",
                19042 => "20H2",
                19041 => "2004",
                18363 => "1909",
                18362 => "1903",
                17763 => "1809",
                17134 => "1803",
                16299 => "1709",
                15063 => "1703",
                14393 => "1607",
                10586 => "1511",
                10240 => "1507",

                _ => null,
            };
        }

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
        /// Reflectively reads a public instance property and returns its raw
        /// value (boxed), or <c>null</c> when the property is missing, the
        /// value is null, or the read throws. Used to walk through complex
        /// SDK shapes like <c>ISystemMetadata.BuildInfo</c> without taking
        /// a hard reference on intermediate interface types.
        /// </summary>
        private static object SafeGetMember(object obj, string propName)
        {
            if (obj == null || string.IsNullOrEmpty(propName)) return null;
            try
            {
                PropertyInfo p = obj.GetType().GetProperty(propName,
                    BindingFlags.Instance | BindingFlags.Public);
                return p?.GetValue(obj);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reflectively reads a property whose value is an integral type
        /// (<c>int</c>, <c>uint</c>, nullable variants, …) and returns it as
        /// <c>int?</c>. Returns <c>null</c> when the property is missing,
        /// the value is null, or the conversion fails.
        /// </summary>
        private static int? SafeReadNullableInt(object obj, string propName)
        {
            object v = SafeGetMember(obj, propName);
            if (v == null) return null;
            try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return null; }
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
