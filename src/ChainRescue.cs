using System;
using EHS;
using UnityEngine;

namespace ChainedIsHard
{
    /// <summary>
    /// What happens when a link is stretched so far that pulling has stopped meaning anything:
    /// one of the two ends is teleported to the other.
    ///
    /// This is what turns a death into a shared one. The game respawns the player who fell at
    /// a checkpoint, which puts them hundreds of metres from the rest of the chain - a distance
    /// no amount of pull is going to close - and the rescue drags everyone else down to them.
    ///
    /// Which end moves is decided locally on both machines, from facts both of them have, so
    /// there is no message to lose and no chance of both sides teleporting at once:
    ///
    ///   1. Just respawned (within RespawnGrace)? Then we are the anchor and do not move. Our
    ///      neighbour, who did not respawn, comes to us.
    ///   2. Otherwise the higher owner id goes to the lower one. Arbitrary, but both machines
    ///      compute the same answer, which is the only property that matters.
    ///
    /// With three or more players it cascades: A respawns, B follows A, and C - now stretched
    /// away from B - follows B.
    /// </summary>
    internal sealed class ChainRescue
    {
        /// <summary>Seconds after a rescue before another one can fire, so the network can catch up.</summary>
        private const float Cooldown = 1.5f;

        /// <summary>How far to the side of the neighbour we land, so we do not spawn inside them.</summary>
        private const float LandingOffset = 1.5f;

        /// <summary>
        /// Extra wait before the lower owner id gives up on the higher one coming.
        ///
        /// The tie-break says the higher id moves, but an anchor never moves whatever its id
        /// is, so when the anchor happens to be the higher one nobody would go. Rather than
        /// telling each other who is anchored - which would need a channel and could be lost -
        /// the other end simply waits this much longer and then goes itself.
        /// </summary>
        private const float DeadlockWait = 1.5f;

        private readonly int[] candidates = new int[2];

        private float stretchedSince = float.NaN;
        private float lastRespawnAt = float.NegativeInfinity;
        private float nextRescueAllowedAt;

        /// <summary>True while our own respawn makes us the end of the chain everyone comes to.</summary>
        public bool IsAnchor { get; private set; }

        /// <summary>
        /// Watches for our own respawn. Called every frame, not in FixedUpdate: the flag is
        /// raised and cleared by the game on its own schedule and a physics step can miss it
        /// entirely.
        /// </summary>
        public void Tick(ChainedSettings settings)
        {
            // Kept fresh for as long as the flag is up, so the grace period starts when the
            // respawn ends rather than when it began.
            if (GameCompat.IsBeingRespawned)
            {
                lastRespawnAt = Time.unscaledTime;
            }

            IsAnchor = Time.unscaledTime - lastRespawnAt <= settings.Value(settings.RespawnGrace);
        }

        public void FixedTick(ChainedSettings settings, ChainTopology topology, NeighbourMotion motion,
            ChainLaunch launch)
        {
            // The rescue stands down during a launch too: mid-arc the two of you are supposed to
            // be far apart, and dragging someone out of the air is the opposite of helping.
            if (!settings.Value(settings.Enabled) || !settings.Value(settings.RescueEnabled) || !topology.Ready ||
                GameRefs.IsBusy || launch.Suspended)
            {
                stretchedSince = float.NaN;
                return;
            }

            try
            {
                Evaluate(settings, topology, motion);
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Chain rescue failed: {ex.Message}");
                stretchedSince = float.NaN;
            }
        }

        private void Evaluate(ChainedSettings settings, ChainTopology topology, NeighbourMotion motion)
        {
            PlayerRef local = GameRefs.LocalPlayer;

            if (!ChainTopology.TryGetPosition(local, out Vector3 here))
            {
                stretchedSince = float.NaN;
                return;
            }

            float breakAt = settings.Value(settings.ChainLength) * settings.Value(settings.RescueDistance);

            int target = -1;
            Vector3 targetPosition = Vector3.zero;
            float worst = breakAt;

            candidates[0] = topology.Previous;
            candidates[1] = topology.Next;

            foreach (int ownerId in candidates)
            {
                if (ownerId < 0)
                {
                    continue;
                }

                PlayerRef neighbour = ChainTopology.Find(ownerId);

                if (!ChainTopology.TryGetPosition(neighbour, out Vector3 there))
                {
                    continue;
                }

                float distance = Vector3.Distance(here, there);

                if (distance > worst)
                {
                    worst = distance;
                    target = ownerId;
                    targetPosition = there;
                }
            }

            if (target < 0)
            {
                stretchedSince = float.NaN;
                return;
            }

            if (float.IsNaN(stretchedSince))
            {
                stretchedSince = Time.unscaledTime;
            }

            // An anchor never moves: the whole point is that the others come down to the
            // checkpoint we just respawned at.
            if (IsAnchor || Time.unscaledTime < nextRescueAllowedAt)
            {
                return;
            }

            // Both ends are stretched and both run this, so the wait is what decides which one
            // goes: the higher owner id first, and the lower one only if that never happened.
            float wait = settings.Value(settings.RescueDelay) +
                (topology.LocalOwnerId < target ? DeadlockWait : 0f);

            if (Time.unscaledTime - stretchedSince < wait)
            {
                return;
            }

            Teleport(local, targetPosition, here, target, worst,
                settings.Value(settings.RescueKeepsSpeed) ? motion.VelocityOf(target) : (Vector3?)null);
        }

        private void Teleport(PlayerRef local, Vector3 targetPosition, Vector3 here, int target, float distance,
            Vector3? inheritedVelocity)
        {
            PlayerTeleportController teleport = local?.PlayerTeleportController;

            if (teleport == null)
            {
                ChainedPlugin.Logger.LogWarning("Chain broke but there is no teleport controller to fix it.");
                stretchedSince = float.NaN;
                return;
            }

            // Landing beside them, on the side we were already on, rather than on top of them.
            Vector3 approach = here - targetPosition;
            approach.y = 0f;

            Vector3 offset = approach.sqrMagnitude > 0.01f
                ? approach.normalized * LandingOffset
                : Vector3.forward * LandingOffset;

            Quaternion rotation = local.transform != null ? local.transform.rotation : Quaternion.identity;

            teleport.TeleportTo(targetPosition + offset, rotation);

            // Landing with their speed, not standing still: a rescue that catches us mid-flight
            // would otherwise drop us out of the air right where they are still travelling.
            if (inheritedVelocity.HasValue)
            {
                Rigidbody rb = local.Rb;

                if (rb != null && !rb.isKinematic)
                {
                    rb.linearVelocity = inheritedVelocity.Value;
                }
            }

            stretchedSince = float.NaN;
            nextRescueAllowedAt = Time.unscaledTime + Cooldown;

            ChainedPlugin.Logger.LogInfo(
                $"Chain broke at {distance:0.#}m, pulled to player {target}.");
        }
    }
}
