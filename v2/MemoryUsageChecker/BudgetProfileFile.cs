// © Microsoft Corporation. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemoryUsageChecker
{
    /// <summary>
    /// Read / write the user-editable <c>MemoryUsageChecker.profiles.json</c>
    /// file shipped next to <c>MemoryUsageChecker.exe</c>. OEMs / ODMs
    /// open this file in any text editor to tighten or relax the per-tier
    /// budgets without touching the source or remembering the command
    /// line — the double-click workflow (the exe auto-discovers the
    /// latest <c>*.etl</c> next to it) then uses those values
    /// automatically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file is canonical: every settable knob from
    /// <see cref="BudgetProfile"/> plus the verdict-percentage thresholds
    /// from <see cref="BudgetProfile.WarnAtPercent"/> and
    /// <see cref="BudgetProfile.FailAtPercent"/> are persisted, with
    /// header comments documenting the meaning of each field. The same
    /// loader powers <c>--write-default-profiles</c> so a corrupted file
    /// can always be regenerated.
    /// </para>
    /// <para>
    /// Lookup precedence at startup, in order:
    /// <list type="number">
    ///   <item><c>--profiles-file &lt;path&gt;</c> if provided.</item>
    ///   <item><c>MemoryUsageChecker.profiles.json</c> next to the running .exe.</item>
    ///   <item><c>MemoryUsageChecker.profiles.json</c> in the current working directory.</item>
    ///   <item>(none — built-in defaults baked into <see cref="BudgetProfile"/>.)</item>
    /// </list>
    /// </para>
    /// </remarks>
    internal static class BudgetProfileFile
    {
        /// <summary>
        /// Canonical file name shipped next to <c>MemoryUsageChecker.exe</c>.
        /// </summary>
        public const string DefaultFileName = "MemoryUsageChecker.profiles.json";

        private const long MiB = 1024L * 1024L;

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Don't escape apostrophes / non-ASCII so the comment strings
            // read naturally in any text editor.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        // ---- File DTO ----------------------------------------------------

        /// <summary>JSON root for <c>MemoryUsageChecker.profiles.json</c>.</summary>
        public sealed class FileModel
        {
            [JsonPropertyName("$comment")]
            public string[] Comment { get; set; } =
            {
                "MemoryUsageChecker default budget profiles.",
                "Edit the numbers below to retune the OEM image-fit verdict; restart the .exe to pick up the changes.",
                "Set 'defaultProfile' to choose which tier runs when no --profile is passed on the command line.",
                "Per-item budgets gate row coloring (red over-budget / yellow near-budget / green under-budget); total budgets gate the per-category PASS / WATCH / FAIL banner.",
                "All MB values are MiB (1 MiB = 1,048,576 bytes).",
            };

            public string SchemaVersion { get; set; } = "1.0";

            public string DefaultProfile { get; set; } = "8gb";

            public VerdictThresholdsModel VerdictThresholds { get; set; } = new VerdictThresholdsModel();

            public Dictionary<string, ProfileModel> Profiles { get; set; } = new Dictionary<string, ProfileModel>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Per-percentage Pass/Warn/Fail thresholds.</summary>
        public sealed class VerdictThresholdsModel
        {
            [JsonPropertyName("$comment")]
            public string Comment { get; set; } = "Percent of the budget at which a row turns yellow (warnAtPercent) or red (failAtPercent). Strictly: actual > failAt = Fail; actual >= warnAt = Warn; else Pass.";

            public int WarnAtPercent { get; set; } = BudgetProfile.DefaultWarnAtPercent;

            public int FailAtPercent { get; set; } = BudgetProfile.DefaultFailAtPercent;
        }

        /// <summary>Per-tier budget values, in MiB.</summary>
        public sealed class ProfileModel
        {
            [JsonPropertyName("$comment")]
            public string Comment { get; set; }

            public int TopProcesses { get; set; }
            public int TopDrivers { get; set; }

            public double PerProcessWorkingSetBudgetMb { get; set; }
            public double PerProcessVirtualAllocBudgetMb { get; set; }
            public double PerDriverPoolBudgetMb { get; set; }
            public double PerDriverCodeBudgetMb { get; set; }

            public double TotalUserWorkingSetBudgetMb { get; set; }
            public double TotalDriverPoolBudgetMb { get; set; }
            public double TotalDriverCodeBudgetMb { get; set; }

            public double MinDisplayMb { get; set; }
        }

        // ---- Default model ----------------------------------------------

        /// <summary>
        /// Build the canonical defaults model — every built-in tier with
        /// its calibrated values. This is what
        /// <c>--write-default-profiles</c> emits and what
        /// <see cref="EnsureFile"/> seeds when the file is missing.
        /// </summary>
        public static FileModel BuildDefaults()
        {
            FileModel m = new FileModel
            {
                DefaultProfile = "8gb",
            };
            m.Profiles["16gb"] = ToModel(BudgetProfile.SixteenGb(),
                "Relaxed tier for higher-end OEM devices with 16 GB total RAM. Use as a soft ceiling so the preload does not bloat over time.");
            m.Profiles["8gb"] = ToModel(BudgetProfile.EightGb(),
                "Default tier. Calibrated against a real Windows 11 24H2 OEM-like trace so the worst contributor in each category turns red.");
            m.Profiles["4gb"] = ToModel(BudgetProfile.FourGb(),
                "Tightest tier. Targets 4 GB-class devices; per-item and total budgets are roughly halved vs the 8 GB tier.");
            return m;
        }

        private static ProfileModel ToModel(BudgetProfile p, string comment) => new ProfileModel
        {
            Comment = comment,
            TopProcesses = p.TopProcesses,
            TopDrivers = p.TopDrivers,
            PerProcessWorkingSetBudgetMb = ToMb(p.PerProcessWorkingSetBudgetBytes),
            PerProcessVirtualAllocBudgetMb = ToMb(p.PerProcessVirtualAllocBudgetBytes),
            PerDriverPoolBudgetMb = ToMb(p.PerDriverPoolBudgetBytes),
            PerDriverCodeBudgetMb = ToMb(p.PerDriverCodeBudgetBytes),
            TotalUserWorkingSetBudgetMb = ToMb(p.TotalUserWorkingSetBudgetBytes),
            TotalDriverPoolBudgetMb = ToMb(p.TotalDriverPoolBudgetBytes),
            TotalDriverCodeBudgetMb = ToMb(p.TotalDriverCodeBudgetBytes),
            MinDisplayMb = ToMb(p.MinDisplayBytes),
        };

        private static double ToMb(long bytes) => bytes / 1048576.0;

        // ---- Lookup + load ----------------------------------------------

        /// <summary>
        /// Resolve the file to load using the precedence documented on
        /// the class summary. Returns <c>null</c> when no file is found
        /// so the caller can fall back to built-in defaults.
        /// </summary>
        public static string ResolvePath(string explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
                return explicitPath;
            }
            string exeDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(exeDir))
            {
                string candidate = Path.Combine(exeDir, DefaultFileName);
                if (File.Exists(candidate)) return candidate;
            }
            string cwd = Directory.GetCurrentDirectory();
            string cwdCandidate = Path.Combine(cwd, DefaultFileName);
            if (File.Exists(cwdCandidate)) return cwdCandidate;
            return null;
        }

        /// <summary>
        /// Load a <see cref="FileModel"/> from disk. Throws on malformed
        /// JSON so the caller can surface a clear error rather than
        /// silently running with built-in defaults.
        /// </summary>
        public static FileModel Load(string path)
        {
            string text = File.ReadAllText(path);
            FileModel model = JsonSerializer.Deserialize<FileModel>(text, ReadOptions);
            if (model == null) throw new InvalidDataException($"Profile file '{path}' parsed to null.");
            if (model.Profiles == null) model.Profiles = new Dictionary<string, ProfileModel>(StringComparer.OrdinalIgnoreCase);
            if (model.VerdictThresholds == null) model.VerdictThresholds = new VerdictThresholdsModel();
            return model;
        }

        /// <summary>
        /// Persist <paramref name="model"/> to disk in pretty-printed JSON.
        /// </summary>
        public static void Save(FileModel model, string path)
        {
            string json = JsonSerializer.Serialize(model, WriteOptions);
            File.WriteAllText(path, json);
        }

        /// <summary>
        /// Make sure a profile file exists at <paramref name="path"/>. When
        /// missing, write the defaults so OEMs always have a template to
        /// edit. Returns <c>true</c> when the file was created by this
        /// call (so the caller can log it), <c>false</c> when it already
        /// existed.
        /// </summary>
        public static bool EnsureFile(string path)
        {
            if (File.Exists(path)) return false;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Save(BuildDefaults(), path);
            return true;
        }

        // ---- Convert FileModel → BudgetProfile --------------------------

        /// <summary>
        /// Build a <see cref="BudgetProfile"/> from a tier in
        /// <paramref name="file"/>. Falls back to the matching built-in
        /// preset when the tier is missing so the run never crashes on a
        /// partial config. Returns <c>null</c> when <paramref name="tier"/>
        /// is unknown to both the file and the built-in presets.
        /// </summary>
        public static BudgetProfile ToProfile(FileModel file, string tier)
        {
            BudgetProfile builtIn = BudgetProfile.TryFromName(tier);
            if (file == null) return builtIn;

            if (!file.Profiles.TryGetValue(tier, out ProfileModel model) || model == null)
            {
                return builtIn;
            }

            // Use the matching built-in as the base so any field the user
            // deleted from the JSON falls back to the calibrated default
            // rather than zero.
            BudgetProfile baseProfile = builtIn ?? BudgetProfile.EightGb();
            return baseProfile with
            {
                Name = tier.ToLowerInvariant(),
                TopProcesses = model.TopProcesses > 0 ? model.TopProcesses : baseProfile.TopProcesses,
                TopDrivers = model.TopDrivers > 0 ? model.TopDrivers : baseProfile.TopDrivers,
                PerProcessWorkingSetBudgetBytes = ToBytes(model.PerProcessWorkingSetBudgetMb, baseProfile.PerProcessWorkingSetBudgetBytes),
                PerProcessVirtualAllocBudgetBytes = ToBytes(model.PerProcessVirtualAllocBudgetMb, baseProfile.PerProcessVirtualAllocBudgetBytes),
                PerDriverPoolBudgetBytes = ToBytes(model.PerDriverPoolBudgetMb, baseProfile.PerDriverPoolBudgetBytes),
                PerDriverCodeBudgetBytes = ToBytes(model.PerDriverCodeBudgetMb, baseProfile.PerDriverCodeBudgetBytes),
                TotalUserWorkingSetBudgetBytes = ToBytes(model.TotalUserWorkingSetBudgetMb, baseProfile.TotalUserWorkingSetBudgetBytes),
                TotalDriverPoolBudgetBytes = ToBytes(model.TotalDriverPoolBudgetMb, baseProfile.TotalDriverPoolBudgetBytes),
                TotalDriverCodeBudgetBytes = ToBytes(model.TotalDriverCodeBudgetMb, baseProfile.TotalDriverCodeBudgetBytes),
                MinDisplayBytes = model.MinDisplayMb > 0 ? (long)(model.MinDisplayMb * MiB) : baseProfile.MinDisplayBytes,
            };
        }

        private static long ToBytes(double mb, long fallbackBytes) =>
            mb > 0 ? (long)(mb * MiB) : fallbackBytes;
    }
}
