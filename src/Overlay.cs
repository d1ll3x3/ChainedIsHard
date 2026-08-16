using System.Text;
using UnityEngine;

namespace ChainedIsHard
{
    /// <summary>
    /// Corner readout: the network role, the chain we are part of and how tight our tightest
    /// link is.
    ///
    /// It is labels and textures only. GUI.Button and GUI.TextField are stripped in this IL2CPP
    /// build and crash the game, so there is deliberately nothing interactive here.
    ///
    /// The chain line is also the diagnostic: if two machines show different orders, the owner
    /// ids the chain is built from are not agreeing and nothing else in the mod can be trusted.
    /// </summary>
    internal sealed class Overlay
    {
        private const float Width = 290f;
        private const float LineHeight = 20f;

        private static readonly Color BarBackground = new(0f, 0f, 0f, 0.45f);
        private static readonly Color BarSlack = new(0.45f, 0.75f, 0.95f, 0.9f);
        private static readonly Color BarTight = new(1f, 0.5f, 0.2f, 0.95f);

        private readonly StringBuilder builder = new();

        private GUIStyle titleStyle;
        private GUIStyle roleStyle;
        private GUIStyle bodyStyle;
        private GUIStyle countdownStyle;
        private GUIStyle countdownHintStyle;

        public void Draw(ChainedSettings settings, ChainTopology topology, ChainSolver solver, ChainRescue rescue,
            ChainLaunch launch, Countdown countdown)
        {
            EnsureStyles();

            // Drawn even with the readout off: it is the one thing three people have to see at
            // the same time, and it is why the key exists.
            DrawCountdown(countdown, launch);

            if (!settings.ShowStatus.Value)
            {
                return;
            }

            float x = Screen.width - Width - 16f;
            float y = 16f;

            GUI.Label(new Rect(x, y, Width, LineHeight), "CHAINED IS HARD", titleStyle);
            y += LineHeight;

            GUI.Label(new Rect(x, y, Width, LineHeight),
                $"{NetRole.Label}   ·   {settings.MenuKey.Value} = menu", roleStyle);
            y += LineHeight * 1.1f;

            if (!settings.Value(settings.Enabled))
            {
                GUI.Label(new Rect(x, y, Width, LineHeight), "chain off", bodyStyle);
                return;
            }

            if (!topology.Ready)
            {
                GUI.Label(new Rect(x, y, Width, LineHeight), "waiting for another player", bodyStyle);
                return;
            }

            GUI.Label(new Rect(x, y, Width, LineHeight), ChainLabel(topology), bodyStyle);
            y += LineHeight * 0.9f;

            GUI.Label(new Rect(x, y, Width, LineHeight),
                $"{solver.LongestLink:0.#}m of {settings.Value(settings.ChainLength):0.#}m" +
                (launch.Suspended ? "   ·   slack for the launch" : string.Empty) +
                (rescue.IsAnchor ? "   ·   anchor" : string.Empty), bodyStyle);
            y += LineHeight * 0.75f;

            DrawBar(new Rect(x, y, Width, 4f), solver.Tension);
        }

        /// <summary>
        /// The shared count, and the cannon's own, in the middle of the screen where nobody has
        /// to look for them.
        /// </summary>
        private void DrawCountdown(Countdown countdown, ChainLaunch launch)
        {
            float seconds = countdown.IsRunning ? countdown.Remaining : launch.CannonCountdown;

            if (seconds <= 0f)
            {
                return;
            }

            var rect = new Rect(0f, Screen.height * 0.28f, Screen.width, 70f);

            // Ceiling, so a count of three reads "3 2 1" and not "2 1 0": what matters is the
            // number you are on, not the fraction left of it.
            GUI.Label(rect, Mathf.CeilToInt(seconds).ToString(), countdownStyle);

            GUI.Label(new Rect(0f, rect.y + 62f, Screen.width, 24f),
                countdown.IsRunning ? "get ready" : "cannon firing", countdownHintStyle);
        }

        /// <summary>The chain as owner ids, with ours in brackets. The order has to match on every machine.</summary>
        private string ChainLabel(ChainTopology topology)
        {
            builder.Clear();

            for (int i = 0; i < topology.Order.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(" — ");
                }

                int id = topology.Order[i];
                builder.Append(id == topology.LocalOwnerId ? $"[{id}]" : id.ToString());
            }

            return builder.ToString();
        }

        private void DrawBar(Rect rect, float tension)
        {
            Gui.Fill(rect, BarBackground);
            Gui.Fill(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(tension), rect.height),
                tension >= 1f ? BarTight : BarSlack);
        }

        private void EnsureStyles()
        {
            if (titleStyle != null)
            {
                return;
            }

            titleStyle = Gui.Style(15, FontStyle.Bold, Gui.Accent, TextAnchor.MiddleRight);
            roleStyle = Gui.Style(11, FontStyle.Normal, Gui.Dimmed, TextAnchor.MiddleRight);
            bodyStyle = Gui.Style(12, FontStyle.Normal, Gui.Normal, TextAnchor.MiddleRight);
            countdownStyle = Gui.Style(64, FontStyle.Bold, Gui.Accent, TextAnchor.MiddleCenter);
            countdownHintStyle = Gui.Style(16, FontStyle.Bold, Gui.Highlight, TextAnchor.MiddleCenter);
        }
    }
}
