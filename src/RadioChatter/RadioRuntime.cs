using BepInEx.Logging;
using RadioChatter.Audio;
using RadioChatter.Game;
using RadioChatter.Speech;

namespace RadioChatter
{
    internal static class RadioRuntime
    {
        private static StatePoller _poller;
        private static RadioAudioPlayer _output;
        private static VoiceCommandController _voice;
        private static Config _config;
        private static ManualLogSource _log;

        public static bool Ready => _poller != null;

        public static void Initialize(StatePoller poller, RadioAudioPlayer output, VoiceCommandController voice, Config config, ManualLogSource log)
        {
            _poller = poller;
            _output = output;
            _voice = voice;
            _config = config;
            _log = log;
        }

        public static void Tick()
        {
            _poller?.Tick();
            _voice?.Tick();
            _output?.SetPushToTalkActive(_voice != null && _voice.IsPushToTalkHeld);
            _output?.Tick();
            ImmersionMapOptions.Tick(_config);
        }

        public static void LogInfoOnce(ref bool flag, string message)
        {
            if (flag)
                return;

            flag = true;
            _log?.LogInfo(message);
        }

        public static void Shutdown()
        {
            _output?.Shutdown();
            _poller = null;
            _output = null;
            _voice = null;
            _config = null;
            _log = null;
        }
    }
}
