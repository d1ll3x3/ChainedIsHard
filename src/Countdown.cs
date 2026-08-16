using UnityEngine;

namespace ChainedIsHard
{
    /// <summary>
    /// A shared "go on three" for the whole lobby.
    ///
    /// Pressing the countdown key starts the same count on everybody's screen. It exists
    /// because the hard part of a cannon or a jump is not the jump, it is agreeing on when -
    /// and voice chat has its own delay. A number counting down in the same place on every
    /// screen is the one thing that lets three people move on the same beat.
    ///
    /// It rides the same SyncVar channel as everything else, with its own tag, and anyone can
    /// start one. A running count cannot be restarted by a later message from the same start,
    /// so a message repeated over several publishes - which is how it survives a dropped
    /// packet - does not keep resetting the clock. The token, not the timestamp, is what makes
    /// one start distinct from another: clocks on two machines never agree, and this way they
    /// do not have to.
    /// </summary>
    internal sealed class Countdown
    {
        /// <summary>How long a start is republished, so a client that missed one publish still gets it.</summary>
        private const float RepeatFor = 0.6f;

        private int token;
        private float endsAt = float.NegativeInfinity;
        private float publishUntil = float.NegativeInfinity;

        /// <summary>Seconds left, or 0 when nothing is counting.</summary>
        public float Remaining => Mathf.Max(0f, endsAt - Time.unscaledTime);

        public bool IsRunning => Remaining > 0f;

        /// <summary>True while the start still has to go out on the wire.</summary>
        public bool NeedsPublishing => Time.unscaledTime < publishUntil;

        /// <summary>The token being published, so the receivers can tell one start from the next.</summary>
        public int Token => token;

        /// <summary>Seconds the count we are publishing runs for.</summary>
        public float Length { get; private set; }

        /// <summary>Starts a count here and marks it for publishing to everyone else.</summary>
        public void Start(float seconds)
        {
            // Anything but zero: zero is what an untouched message reads as, and a token that
            // cannot be told from silence would fire a countdown on every client that joins.
            token = token % 4095 + 1;
            Length = seconds;
            endsAt = Time.unscaledTime + seconds;
            publishUntil = Time.unscaledTime + RepeatFor;

            ChainedPlugin.Logger.LogInfo($"Countdown started: {seconds:0.#}s.");
        }

        /// <summary>Starts a count somebody else called. Repeats of one already running are ignored.</summary>
        public void Heard(int remoteToken, float seconds)
        {
            if (remoteToken == token && IsRunning)
            {
                return;
            }

            token = remoteToken;
            Length = seconds;
            endsAt = Time.unscaledTime + seconds;

            // Not republished: it is not ours to repeat, and two clients echoing each other
            // would keep the count alive forever.
            publishUntil = float.NegativeInfinity;

            ChainedPlugin.Logger.LogInfo($"Countdown called by another player: {seconds:0.#}s.");
        }

        public void Clear()
        {
            endsAt = float.NegativeInfinity;
            publishUntil = float.NegativeInfinity;
        }
    }
}
