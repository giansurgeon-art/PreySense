using System;

namespace PreySense
{
    /// <summary>
    /// Diagnostic kill switches, read once from the PREYSENSE_DISABLE environment variable
    /// (comma-separated). Used to find which PreySense feature interferes with something
    /// else on the machine. Recognized names: displaywatch, gpu, dds.
    /// Example: set PREYSENSE_DISABLE=displaywatch,dds
    /// </summary>
    public static class DiagSwitches
    {
        private static readonly string[] Active = Parse();

        public static bool Has(string name) => Array.IndexOf(Active, name) >= 0;

        private static string[] Parse()
        {
            string? raw = Environment.GetEnvironmentVariable("PREYSENSE_DISABLE");
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

            string[] parts = raw.ToLowerInvariant().Split(
                new[] { ',', ';', ' ' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AppLogger.Error($"DIAG: PREYSENSE_DISABLE active: {string.Join(",", parts)}");
            return parts;
        }
    }
}

