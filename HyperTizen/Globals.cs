using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tizen.Applications;
using HyperTizen.Bulbs;

namespace HyperTizen
{
    public sealed class Globals
    {
        // FILESTEALER MODE: When enabled, the app will copy Tizen OS files to USB and exit
        // This is for research purposes on Tizen 9+ to understand OS structure
        // WARNING: Set to false for normal capture operation!
        public const bool FILESTEALER_ENABLED = false;

        // DIAGNOSTIC MODE BUILD CONST: When enabled, diagnostic mode will be enabled by default
        // Set to true during development/debugging to enable 10-minute diagnostic pause
        // Set to false for production builds
        public const bool DIAGNOSTIC_MODE_ENABLED = false;

        private static readonly Globals instance = new Globals();

        // Explicit static constructor to tell C# compiler
        // not to mark type as beforefieldinit
        static Globals()
        {
        }

        private Globals()
        {
        }

        public static Globals Instance
        {
            get
            {
                return instance;
            }
        }
        public void LoadPreferencesEarly()
        {
            // Load enabled and diagnosticMode BEFORE any testing
            string enabledPref = Preference.Contains("enabled") ? Preference.Get<string>("enabled") : "false";
            Enabled = bool.TryParse(enabledPref, out bool enabledResult) && enabledResult;

            string diagnosticPref = Preference.Contains("diagnosticMode") ? Preference.Get<string>("diagnosticMode") : "false";
            DiagnosticMode = bool.TryParse(diagnosticPref, out bool diagnosticResult) && diagnosticResult;

            // Bulb tuning: a missing or invalid key keeps its default
            var tuning = Tuning.Default;
            foreach (string key in TuningKeys)
            {
                if (!Preference.Contains(key)) continue;
                string value = Preference.Get<string>(key);
                if (!Tuning.TryParse(key, value, tuning, out tuning))
                    Helper.Log.Write(Helper.eLogType.Warning, $"Ignoring invalid saved {key}={value}, using default");
            }
            Tuning = tuning;

            Helper.Log.Write(Helper.eLogType.Info, $"Preferences loaded early - DiagnosticMode: {DiagnosticMode}, Enabled: {Enabled}, Tuning: {Tuning}");
        }

        public static readonly string[] TuningKeys = { "bulbGamma", "bulbSmoothMs", "bulbMinLevel", "bulbBlack" };

        public string ServerIp; //IP of hyperhdr server (unused: frames go to the bulbs)
        public int ServerPort; //Port of hyperhdr server (unused)
        public int Width = 3840/8; //Capture Width
        public int Height = 2160/8; //Capture Height
        public volatile Tuning Tuning = Tuning.Default; //Bulb tuning, read by BulbSync every send cycle
        public bool Enabled; //Persisted "enabled" mirror; App.Configuration.Enabled is what Start() checks
        public bool DiagnosticMode; //Enable diagnostic mode (pauses for 10 minutes after initialization)
        public bool ShowNotifications = false; //Show notification popups on TV (default: false, use new UI instead)
    }
}
