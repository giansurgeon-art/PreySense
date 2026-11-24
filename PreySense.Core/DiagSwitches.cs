using System;

namespace PreySense
{
    /// <summary>
    /// Lite-mode switches. By default PreySense runs without GPU sensor polling, NVIDIA
    /// display-mode (DDS) access and display-change watching, so it never touches the NVIDIA
    /// driver's automatic iGPU/dGPU switching. Override with the PREYSENSE_DISABLE environment
    /// variable (comma-separated: displaywatch, gpu, dds), or set it to "none" to turn every
    /// feature back on.
    /// </summary>
    public static class DiagSwitches
    {
        private static readonly string[] DefaultSwitches = { "displaywatch", "gpu", "dds" };
        private static readonly string[] Active = Parse();

        public static bool Has(string name) => Array.IndexOf(Active, name) >= 0;

        private static string[] Parse()
        {
            string? raw = Environment.GetEnvironmentVariable("PREYSENSE_DISABLE");
            string[] parts;
            if (string.IsNullOrWhiteSpace(raw))
            {
                parts = DefaultSwitches;
            }
            else if (string.Equals(raw.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<string>();
            }
            else
            {
                parts = raw.ToLowerInvariant().Split(
                    new[] { ',', ';', ' ' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            AppLogger.Error($"DIAG: lite mode active, disabled: {string.Join(",", parts)}");
            return parts;
        }
    }
}
