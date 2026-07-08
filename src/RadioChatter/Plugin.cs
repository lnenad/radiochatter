using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RadioChatter.Audio;
using RadioChatter.Comms;
using RadioChatter.Game;
using UnityEngine;

namespace RadioChatter
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.lnenad.radiochatter";
        public const string Name = "RadioChatter";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static Config Cfg;

        private Harmony _harmony;
        private StatePoller _poller;
        private RadioAudioPlayer _output;

        private void Awake()
        {
            Log = Logger;
            Cfg = new Config(base.Config);

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(KillDisplayPatch).Assembly);

            _output = new RadioAudioPlayer();
            _output.Initialize(Cfg, gameObject, Log);

            CommsDirector director = new CommsDirector(Cfg, _output, Log);
            _poller = new StatePoller();
            _poller.Initialize(new GameAdapter(), director, Cfg, Log);
            RadioRuntime.Initialize(_poller, _output, Cfg, Log);

            Log.LogInfo($"{Name} {Version} loaded");
            Log.LogInfo($"Config: Enabled={Cfg.Enabled.Value}, DebugOverlay={Cfg.DebugOverlay.Value}, SubtitlesEnabled={Cfg.SubtitlesEnabled.Value}");
        }

        private void Update()
        {
            RadioRuntime.Tick();
        }

        private void OnGUI()
        {
            _output?.DrawGui();
        }

        private void OnDestroy()
        {
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
                _harmony = null;
            }

            RadioRuntime.Shutdown();
        }
    }

    /// <summary>Typed wrapper over all BepInEx config entries (§10 of the plan).</summary>
    internal sealed class Config
    {
        // General
        public readonly ConfigEntry<bool> Enabled;
        public readonly ConfigEntry<string> PlayerCallsign;
        public readonly ConfigEntry<string> AwacsCallsign;
        public readonly ConfigEntry<bool> SubtitlesEnabled;
        public readonly ConfigEntry<float> PollIntervalSeconds;
        public readonly ConfigEntry<bool> DebugOverlay;

        // Sidecar
        public readonly ConfigEntry<string> SidecarUrl;
        public readonly ConfigEntry<bool> AutoStartSidecar;
        public readonly ConfigEntry<string> SidecarCommand;
        public readonly ConfigEntry<bool> StopSidecarOnExit;
        public readonly ConfigEntry<int> CacheSize;

        // Audio
        public readonly ConfigEntry<float> Volume;
        public readonly ConfigEntry<bool> RadioEffectEnabled;
        public readonly ConfigEntry<float> NoiseLevel;
        public readonly ConfigEntry<int> MaxConcurrentTransmissions;
        public readonly ConfigEntry<string> TowerVoice;
        public readonly ConfigEntry<string> AwacsVoice;
        public readonly ConfigEntry<string> PlayerVoice;
        public readonly ConfigEntry<string> WingmanVoice;

        // Callouts
        public readonly ConfigEntry<bool> TakeoffCalls;
        public readonly ConfigEntry<bool> LandingCalls;
        public readonly ConfigEntry<bool> ApproachCalls;
        public readonly ConfigEntry<bool> NewContactCalls;
        public readonly ConfigEntry<float> ContactInfoCooldownSeconds;
        public readonly ConfigEntry<bool> PictureUpdateCalls;
        public readonly ConfigEntry<float> PictureIntervalSeconds;
        public readonly ConfigEntry<bool> VectorToTargetCalls;
        public readonly ConfigEntry<float> VectorIntervalSeconds;
        public readonly ConfigEntry<bool> SplashCalls;
        public readonly ConfigEntry<bool> MissileWarnings;
        public readonly ConfigEntry<bool> PlayerWeaponCalls;
        public readonly ConfigEntry<bool> PlayerDefensiveCalls;
        public readonly ConfigEntry<bool> PlayerEjectionCalls;
        public readonly ConfigEntry<bool> PlayerAcknowledgements;
        public readonly ConfigEntry<bool> InGameComms;
        public readonly ConfigEntry<bool> RtbCalls;

        public Config(ConfigFile f)
        {
            Enabled = f.Bind("General", "Enabled", true, "Master switch for all RadioChatter functionality.");
            PlayerCallsign = f.Bind("General", "PlayerCallsign", "Falcon 1-1", "How Tower/AWACS address the player.");
            AwacsCallsign = f.Bind("General", "AwacsCallsign", "Overwatch", "AWACS station callsign.");
            SubtitlesEnabled = f.Bind("General", "SubtitlesEnabled", true, "Show callout text on screen.");
            PollIntervalSeconds = f.Bind("General", "PollIntervalSeconds", 0.5f,
                new ConfigDescription("Game-state polling interval.", new AcceptableValueRange<float>(0.1f, 2f)));
            DebugOverlay = f.Bind("General", "DebugOverlay", false, "Show live game-state debug overlay (Phase 1 verification).");

            SidecarUrl = f.Bind("Sidecar", "Url", "http://127.0.0.1:5075", "Base URL of the Pocket TTS sidecar.");
            AutoStartSidecar = f.Bind("Sidecar", "AutoStartSidecar", true, "Launch the Pocket TTS sidecar automatically if /health is down.");
            SidecarCommand = f.Bind("Sidecar", "SidecarCommand", "", "Path to a sidecar launcher script used when AutoStartSidecar is on. Empty tries sidecar/run_sidecar.bat or sidecar/run_sidecar.sh next to the plugin.");
            StopSidecarOnExit = f.Bind("Sidecar", "StopSidecarOnExit", true, "Stop the auto-started sidecar when the game exits. A sidecar you started manually is never touched.");
            CacheSize = f.Bind("Sidecar", "CacheSize", 100, "Max synthesized clips kept in the LRU cache.");

            Volume = f.Bind("Audio", "Volume", 0.8f,
                new ConfigDescription("Radio voice volume.", new AcceptableValueRange<float>(0f, 1f)));
            RadioEffectEnabled = f.Bind("Audio", "RadioEffectEnabled", true, "Apply subtle bandpass/noise radio processing.");
            NoiseLevel = f.Bind("Audio", "NoiseLevel", 0.015f,
                new ConfigDescription("Background transmission hiss amount. Applied very lightly.", new AcceptableValueRange<float>(0f, 0.2f)));
            MaxConcurrentTransmissions = f.Bind("Audio", "MaxConcurrentTransmissions", 3,
                new ConfigDescription("Maximum RadioChatter voice clips that may overlap.", new AcceptableValueRange<int>(1, 6)));
            TowerVoice = f.Bind("Audio", "TowerVoice", "tower", "Pocket TTS voice id used for Tower.");
            AwacsVoice = f.Bind("Audio", "AwacsVoice", "awacs", "Pocket TTS voice id used for AWACS.");
            PlayerVoice = f.Bind("Audio", "PlayerVoice", "player", "Pocket TTS voice id used for player/pilot calls.");
            WingmanVoice = f.Bind("Audio", "WingmanVoice", "wingman", "Pocket TTS voice id used for captured in-game / wingman comms.");

            TakeoffCalls = f.Bind("Callouts", "Takeoff", true, "Tower takeoff clearance / airborne calls.");
            LandingCalls = f.Bind("Callouts", "Landing", true, "Tower cleared-to-land / welcome-home calls.");
            ApproachCalls = f.Bind("Callouts", "Approach", true, "Tower approach calls.");
            NewContactCalls = f.Bind("Callouts", "NewContact", true, "AWACS new-contact BRA calls.");
            ContactInfoCooldownSeconds = f.Bind("Callouts", "ContactInfoCooldownSeconds", 35f,
                new ConfigDescription(
                    "Minimum seconds between AWACS calls about the same contact (new contact, picture, vector), unless its range or aspect changes significantly.",
                    new AcceptableValueRange<float>(10f, 180f)));
            PictureUpdateCalls = f.Bind("Callouts", "PictureUpdate", true, "Periodic AWACS picture updates.");
            PictureIntervalSeconds = f.Bind("Callouts", "PictureIntervalSeconds", 45f,
                new ConfigDescription("Minimum seconds between picture updates.", new AcceptableValueRange<float>(15f, 300f)));
            VectorToTargetCalls = f.Bind("Callouts", "VectorToTarget", true, "AWACS vectors to the player's selected target.");
            VectorIntervalSeconds = f.Bind("Callouts", "VectorIntervalSeconds", 20f,
                new ConfigDescription("Minimum seconds between vector calls.", new AcceptableValueRange<float>(10f, 120f)));
            SplashCalls = f.Bind("Callouts", "SplashCalls", true, "Splash / good-effect kill confirmations.");
            MissileWarnings = f.Bind("Callouts", "MissileWarning", true, "Defend calls for missiles targeting the player.");
            PlayerWeaponCalls = f.Bind("Callouts", "PlayerWeaponCalls", true, "Pilot weapon release calls such as fox, rifle, magnum, pickle, and guns.");
            PlayerDefensiveCalls = f.Bind("Callouts", "PlayerDefensiveCalls", true, "Pilot defensive calls for incoming missiles.");
            PlayerEjectionCalls = f.Bind("Callouts", "PlayerEjectionCalls", true, "Pilot mayday call when the player ejects.");
            PlayerAcknowledgements = f.Bind("Callouts", "PlayerAcknowledgements", true, "Short varied pilot acknowledgements after incoming radio calls finish.");
            InGameComms = f.Bind("Callouts", "InGameComms", true, "Read mission-scripted in-game comms, such as AI wingman radio messages.");
            RtbCalls = f.Bind("Callouts", "RtbCalls", true, "Low-fuel and sustained inbound return-to-base advisories.");
        }
    }
}
