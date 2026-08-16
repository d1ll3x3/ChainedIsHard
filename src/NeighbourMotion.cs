using System.Collections.Generic;
using EHS;
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
    /// </summary>
    internal sealed class NeighbourMotion
    {
        /// <summary>Metres in one physics step past which a player teleported rather than moved.</summary>
        private const float MaxSampledStep = 5f;

        private struct Sample
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public float Time;
        }

        private readonly Dictionary<int, Sample> samples = new();

        /// <summary>Takes this step's reading of everyone in the chain. Call once per FixedUpdate.</summary>
        public void Tick(ChainTopology topology)
        {
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

        /// <summary>How a player is moving, or zero when we have nothing to measure it from.</summary>
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

                // A jump too big to be movement is a teleport or a respawn, and is worth no
                // velocity at all.
                if (travelled.magnitude <= MaxSampledStep)
                {
                    velocity = travelled / (now - previous.Time);
                }
            }

            samples[ownerId] = new Sample { Position = position, Velocity = velocity, Time = now };
        }
    }
}
