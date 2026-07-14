using RadioChatter.Speech;
using UnityEngine;

namespace RadioChatter.Audio
{
    /// <summary>Bottom-right status chip describing sidecar readiness; the "ready" banner
    /// fades a few seconds after the sidecar becomes available.</summary>
    internal sealed class SidecarStatusHud
    {
        private const float ReadyBannerSeconds = 5f;

        private GUIStyle _statusStyle;
        private SidecarSupervisor.SidecarStatus _lastStatus = SidecarSupervisor.SidecarStatus.Unknown;
        private float _readyAt = float.NaN;

        public void Draw(SidecarSupervisor sidecar, float now)
        {
            if (sidecar == null)
                return;

            SidecarSupervisor.SidecarStatus status = sidecar.Status;
            if (status != _lastStatus)
            {
                if (status == SidecarSupervisor.SidecarStatus.Available)
                    _readyAt = now;
                _lastStatus = status;
            }

            string text;
            switch (status)
            {
                case SidecarSupervisor.SidecarStatus.Available:
                    if (float.IsNaN(_readyAt) || now - _readyAt > ReadyBannerSeconds)
                        return;
                    text = "Radio voice: ready";
                    break;
                case SidecarSupervisor.SidecarStatus.Starting:
                    text = "Radio voice: starting sidecar...";
                    break;
                case SidecarSupervisor.SidecarStatus.DownloadingModel:
                    text = "Radio voice: downloading voice model (first run)...";
                    break;
                case SidecarSupervisor.SidecarStatus.LoadingModel:
                    text = "Radio voice: loading TTS model...";
                    break;
                case SidecarSupervisor.SidecarStatus.Unavailable:
                    text = "Radio voice: sidecar unavailable - subtitles only";
                    break;
                default:
                    text = "Radio voice: connecting to sidecar...";
                    break;
            }

            if (_statusStyle == null)
            {
                _statusStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fontSize = 13
                };
                _statusStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f, 0.95f);
            }

            Vector2 size = _statusStyle.CalcSize(new GUIContent(text));
            Rect rect = new Rect(Screen.width - size.x - 32f, Screen.height - 34f, size.x + 16f, 24f);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + 8f, rect.y + 2f, size.x, 20f), text, _statusStyle);
        }
    }
}
