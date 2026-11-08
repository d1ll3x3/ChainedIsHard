using System;
using FishNet;
using FishNet.Managing.Timing;
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
    /// Which is why it has to end on the same beat everywhere, not merely start. A count is
    /// named by the network tick it was called on, and every machine works out from that tick
    /// how much of it is left: the time the call spent getting here comes off the count instead
    /// of being added to the end of it. FishNet keeps every client's Tick in step with the
    /// server's, so the tick is a clock all the machines share - unlike their own clocks, which
    /// never agree.
    ///
    /// The tick also tells one call from the next: a repeat of the count already running, such
    /// as our own call echoed back to us, is ignored.
    /// </summary>
    internal sealed class Countdown
    {
        /// <summary>
        /// Ticks are sent modulo this, so a call fits in the ushort it travels as. Half of it is
        /// how late a call may arrive and still be read the right way round - many seconds at
        /// any tick rate, far beyond what a message spends in transit.
        /// </summary>
        public const int Window = 2048;

        private int call = -1;
        private float endsAt = float.NegativeInfinity;

        /// <summary>Seconds left, or 0 when nothing is counting.</summary>
        public float Remaining => Mathf.Max(0f, endsAt - Time.unscaledTime);

        public bool IsRunning => Remaining > 0f;

        /// <summary>
        /// Starts a count here, now. Returns the call to send to everyone else, or -1 when there
        /// is no network clock to name it by and the count stays on this screen.
        /// </summary>
        public int Call(float seconds)
        {
            TimeManager clock = Clock();

            if (clock == null)
            {
                Run(-1, seconds);
                ChainedPlugin.Logger.LogWarning("Countdown started on this screen only: there is no network clock.");
                return -1;
            }

            int now = (int)(clock.Tick % Window);
            Run(now, seconds);

            ChainedPlugin.Logger.LogInfo($"Countdown called: {seconds:0.#}s.");
            return now;
        }

        /// <summary>A count somebody called - possibly us, echoed back. Runs whatever is left of it.</summary>
        public void Heard(int calledAt, float seconds)
        {
            if (calledAt == call && IsRunning)
            {
                return;
            }

            TimeManager clock = Clock();

            if (clock == null)
            {
                return;
            }

            int age = ((int)(clock.Tick % Window) - calledAt + Window) % Window;

            // Our estimate of the server tick can sit a tick or two behind the caller's, which
            // makes a fresh call look almost a whole window old. Past half the window it is that.
            if (age > Window / 2)
            {
                age -= Window;
            }

            float left = seconds - (float)(age * clock.TickDelta);

            if (left <= 0f)
            {
                return;
            }

            Run(calledAt, left);
            ChainedPlugin.Logger.LogInfo($"Countdown called by another player: {left:0.##}s left of {seconds:0.#}s.");
        }

        public void Clear()
        {
            call = -1;
            endsAt = float.NegativeInfinity;
        }

        private void Run(int calledAt, float seconds)
        {
            call = calledAt;
            endsAt = Time.unscaledTime + seconds;
        }

        private static TimeManager Clock()
        {
            try
            {
                return InstanceFinder.TimeManager;
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogWarning($"Could not read the network clock: {ex.Message}");
                return null;
            }
        }
    }
}
