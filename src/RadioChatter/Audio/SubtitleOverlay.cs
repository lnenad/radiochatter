using RadioChatter.Comms;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RadioChatter.Audio
{
    /// <summary>Bottom-center subtitle stack: prunes expired lines, keeps readback/check-in
    /// prompts pinned, and renders via IMGUI. Main-thread only (OnGUI + Tick). The caller
    /// owns the pause-aware clock and passes it in.</summary>
    internal sealed class SubtitleOverlay
    {
        private const float ReadbackSubtitleSafetySeconds = 45f;
        private const int MaxActiveSubtitles = 3;

        private readonly List<SubtitleLine> _subtitles = new List<SubtitleLine>(MaxActiveSubtitles);
        private GUIStyle _style;
        private GUIStyle _readbackIconStyle;

        public void Add(
            string text,
            float now,
            float displaySeconds,
            bool requiresReadback = false,
            TowerReadbackKind readbackKind = default,
            bool isAwacsCheckInPrompt = false)
        {
            Prune(now);

            if (requiresReadback)
                ClearReadbackPrompt(readbackKind);

            while (_subtitles.Count >= MaxActiveSubtitles)
            {
                int removeIndex = 0;
                for (int i = 0; i < _subtitles.Count; i++)
                {
                    if (!_subtitles[i].RequiresReadback && !_subtitles[i].IsAwacsCheckInPrompt)
                    {
                        removeIndex = i;
                        break;
                    }
                }

                _subtitles.RemoveAt(removeIndex);
            }

            _subtitles.Add(new SubtitleLine
            {
                Text = text,
                Until = isAwacsCheckInPrompt
                    ? float.PositiveInfinity
                    : now + (requiresReadback
                        ? Mathf.Max(ReadbackSubtitleSafetySeconds, displaySeconds)
                        : Mathf.Max(1f, displaySeconds)),
                RequiresReadback = requiresReadback,
                ReadbackKind = readbackKind,
                IsAwacsCheckInPrompt = isAwacsCheckInPrompt
            });
        }

        public void ClearReadbackPrompt(TowerReadbackKind kind)
        {
            for (int i = _subtitles.Count - 1; i >= 0; i--)
            {
                if (_subtitles[i].RequiresReadback && _subtitles[i].ReadbackKind == kind)
                    _subtitles.RemoveAt(i);
            }
        }

        public void ShowAwacsCheckInPrompt(string awacsCallsign, string playerCallsign, float now)
        {
            string awacs = string.IsNullOrWhiteSpace(awacsCallsign) ? "AWACS" : awacsCallsign.Trim();
            string player = string.IsNullOrWhiteSpace(playerCallsign) ? "your callsign" : playerCallsign.Trim();
            string text = $"[RADIO] Report to {awacs}: \"{awacs}, {player}, checking in\"";

            for (int i = 0; i < _subtitles.Count; i++)
            {
                if (!_subtitles[i].IsAwacsCheckInPrompt)
                    continue;

                SubtitleLine existing = _subtitles[i];
                existing.Text = text;
                existing.Until = float.PositiveInfinity;
                _subtitles[i] = existing;
                return;
            }

            Add(text, now, 1f, isAwacsCheckInPrompt: true);
        }

        public void ClearAwacsCheckInPrompt()
        {
            for (int i = _subtitles.Count - 1; i >= 0; i--)
            {
                if (_subtitles[i].IsAwacsCheckInPrompt)
                    _subtitles.RemoveAt(i);
            }
        }

        public void Clear()
        {
            _subtitles.Clear();
        }

        public void Draw(float now)
        {
            Prune(now);
            if (_subtitles.Count == 0)
                return;

            EnsureStyles();
            DrawContainer(BuildText(), HasActionPrompt());
        }

        public static string PrefixForRole(RadioRole role)
        {
            if (role == RadioRole.Tower)
                return "[TWR]";

            if (role == RadioRole.Awacs)
                return "[AWACS]";

            if (role == RadioRole.Player)
                return "[PILOT]";

            if (role == RadioRole.PlayerTower)
                return "[PLAYER-TWR]";

            if (role == RadioRole.PlayerFlight)
                return "[PLAYER-FLIGHT]";

            if (role == RadioRole.PlayerAwacs)
                return "[PLAYER-AWACS]";

            if (role == RadioRole.Game)
                return "[COMMS]";

            return "[RadioChatter]";
        }

        private void Prune(float now)
        {
            for (int i = _subtitles.Count - 1; i >= 0; i--)
            {
                if (now > _subtitles[i].Until)
                    _subtitles.RemoveAt(i);
            }
        }

        private bool HasActionPrompt()
        {
            for (int i = _subtitles.Count - 1; i >= 0; i--)
            {
                if (_subtitles[i].RequiresReadback || _subtitles[i].IsAwacsCheckInPrompt)
                    return true;
            }

            return false;
        }

        private string BuildText()
        {
            StringBuilder builder = new StringBuilder(160);
            for (int i = 0; i < _subtitles.Count; i++)
            {
                if (builder.Length > 0)
                    builder.Append('\n');
                builder.Append(_subtitles[i].Text);
            }

            return builder.ToString();
        }

        private void EnsureStyles()
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 18,
                    wordWrap = true
                };
                _style.normal.textColor = Color.white;
            }

            if (_readbackIconStyle == null)
            {
                _readbackIconStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 14,
                    fontStyle = FontStyle.Bold
                };
                _readbackIconStyle.normal.textColor = new Color(0.12f, 0.07f, 0.01f, 1f);
            }
        }

        private void DrawContainer(string subtitle, bool requiresReadback)
        {
            float width = Mathf.Min(Screen.width - 40f, 900f);
            float leftInset = requiresReadback ? 48f : 12f;
            float labelWidth = width - leftInset - 12f;
            float labelHeight = _style.CalcHeight(new GUIContent(subtitle), labelWidth);
            float height = Mathf.Clamp(labelHeight + 16f, 70f, 220f);
            Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height - height - 40f, width, height);

            if (requiresReadback)
            {
                DrawSolidRect(rect, new Color(0.14f, 0.09f, 0.025f, 0.92f));
                Rect icon = new Rect(rect.x + 12f, rect.center.y - 11f, 22f, 22f);
                DrawSolidRect(icon, new Color(0.9f, 0.56f, 0.16f, 1f));
                GUI.Label(icon, "!", _readbackIconStyle);
            }
            else
            {
                GUI.Box(rect, GUIContent.none);
            }

            GUI.Label(new Rect(rect.x + leftInset, rect.y + 8f, labelWidth, rect.height - 16f), subtitle, _style);
        }

        private static void DrawSolidRect(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private struct SubtitleLine
        {
            public string Text;
            public float Until;
            public bool RequiresReadback;
            public TowerReadbackKind ReadbackKind;
            public bool IsAwacsCheckInPrompt;
        }
    }
}
