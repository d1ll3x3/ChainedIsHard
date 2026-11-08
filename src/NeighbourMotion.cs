using System;
using System.Collections.Generic;
using EHS;
using FishNet;
using FishNet.Managing.Timing;
using UnityEngine;

namespace ChainedIsHard
{
    /// <summary>
    /// Where the other players are and how they are moving.
    ///
    /// Their rigidbody velocity is not ours to read in any meaningful sense - remote players
    /// are driven by their NetworkTransform here, not simulated - so it is measured off the
    /// interpolated positions we do see, once per physics step.
    ///
    /// It lives in one place because three things need it and they must all see the same
    /// numbers: sampling the same player from the solver, the rescue and the launch sharing
    /// would have each of them measuring a different fraction of the same movement.
    ///
    /// The velocity is smoothed. Interpolated positions arrive in small bursts whenever the
    /// network does, and a raw step-to-step difference turns every burst into a spike that the
    /// rope would read as the neighbour lunging away.
    ///
    /// And what we see of a neighbour is where they were a moment ago - their latency plus the
    /// interpolation buffer. For the rope that is not a small error: two players swinging round
    /// each other each pull towards where the other was, which is slightly ahead of the true
    /// centre, so every pull speeds the swing up and the pair spins faster and faster. The rope
    /// uses TryGetPredicted instead, which carries them forward by that delay.
    /// </summary>
    internal sealed class NeighbourMotion
    {
        /// <summary>Metres in one physics step past which a player teleported rather than moved.</summary>
        private const float MaxSampledStep = 5f;

        /// <summary>Seconds the velocity takes to follow a change: enough to flatten a burst, short enough to keep up.</summary>
        private const float SmoothingTime = 0.1f;

        /// <summary>
        /// Ticks FishNet's NetworkTransform holds a remote player back by to interpolate them -
        /// its default - on top of the network latency itself.
        /// </summary>
        private const int InterpolationTicks = 2;

        /// <summary>One-way latency assumed when there is no round trip to measure: on the host, which has none of its own.</summary>
        private const float HostLatency = 0.08f;

        /// <summary>Seconds of prediction past which a guess is worse than the stale position.</summary>
        private const float MaxLag = 0.3f;

        /// <summary>Metres a prediction may move a neighbour, so one bad velocity reading cannot fling the rope.</summary>
        private const float MaxPrediction = 3f;

        private struct Sample
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public float Time;
        }

        private readonly Dictionary<int, Sample> samples = new();

        private bool warnedClock;

        /// <summary>Seconds behind the truth that what we see of a remote player is, as best we can tell.</summary>
        public float Lag { get; private set; }

        /// <summary>Takes this step's reading of everyone in the chain. Call once per FixedUpdate.</summary>
        public void Tick(ChainTopology topology)
        {
            Lag = EstimateLag();

            IReadOnlyList<int> order = topology.Order;

            for (int i = 0; i < order.Count; i++)
            {
                int ownerId = order[i];
                PlayerRef player = ChainTopology.Find(ownerId);

                if (ChainTopology.TryGetPosition(player, out Vector3 position))
                {
                    Record(ownerId, position);
                }
                else
                {
                    // Gone for now - respawning, or not spawned yet. Dropped rather than kept,
                    // so the position they come back at is not read as movement.
                    samples.Remove(ownerId);
                }
            }
        }

        public bool TryGetPosition(int ownerId, out Vector3 position)
        {
            if (samples.TryGetValue(ownerId, out Sample sample))
            {
                position = sample.Position;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Where a remote player most likely is now, rather than where we see them: carried
        /// forward along their velocity by the lag, never by more than MaxPrediction.
        /// </summary>
        public bool TryGetPredicted(int ownerId, out Vector3 position)
        {
            if (!samples.TryGetValue(ownerId, out Sample sample))
            {
                position = Vector3.zero;
                return false;
            }

            position = sample.Position + Vector3.ClampMagnitude(sample.Velocity * Lag, MaxPrediction);
            return true;
        }

        /// <summary>How a player is moving, smoothed, or zero when we have nothing to measure it from.</summary>
        public Vector3 VelocityOf(int ownerId) =>
            samples.TryGetValue(ownerId, out Sample sample) ? sample.Velocity : Vector3.zero;

        public void Clear() => samples.Clear();

        private void Record(int ownerId, Vector3 position)
        {
            float now = Time.fixedTime;
            Vector3 velocity = Vector3.zero;

            if (samples.TryGetValue(ownerId, out Sample previous) && now > previous.Time)
            {
                Vector3 travelled = position - previous.Position;
                float elapsed = now - previous.Time;

                // A jump too big to be movement is a teleport or a respawn, and is worth no
                // velocity at all - nor is anything measured before it.
                if (travelled.magnitude <= MaxSampledStep)
                {
                    float follow = 1f - Mathf.Exp(-elapsed / SmoothingTime);
                    velocity = Vector3.Lerp(previous.Velocity, travelled / elapsed, follow);
                }
            }

            samples[ownerId] = new Sample { Position = position, Velocity = velocity, Time = now };
        }

        /// <summary>
        /// Half the round trip plus the interpolation buffer. The host has no round trip of its
        /// own to measure, so it assumes a typical one rather than none.
        /// </summary>
        private float EstimateLag()
        {
            try
            {
                TimeManager clock = InstanceFinder.TimeManager;

                if (clock == null)
                {
                    return 0f;
                }

                float roundTrip = clock.RoundTripTime / 1000f;
                float oneWay = roundTrip > 0f ? roundTrip * 0.5f : HostLatency;

                return Mathf.Min(MaxLag, oneWay + InterpolationTicks * (float)clock.TickDelta);
            }
            catch (Exception ex)
            {
                if (!warnedClock)
                {
                    warnedClock = true;
                    ChainedPlugin.Logger.LogWarning($"Could not read the network latency, the rope will not predict: {ex.Message}");
                }

                return 0f;
            }
        }
    }
}
