using System;
using System.Collections.Generic;
using EHS;
using UnityEngine;

namespace ChainedIsHard
{
    /// <summary>
    /// The chain itself: a maximum distance to each neighbour, enforced in FixedUpdate.
    ///
    /// Only the local rigidbody is ever touched. Remote players are not simulated here - their
    /// NetworkTransform drives them - so pushing them around locally would only fight the next
    /// packet and look like rubber banding. Both ends run this same solver against each other,
    /// each correcting its Share of the overshoot, and the two halves add up to a link that
    /// holds.
    ///
    /// Inside the length nothing happens at all, and past it the speed you are moving away at
    /// is cancelled and you are pulled back. How hard depends on Elasticity: at 0 the whole
    /// stretch is taken out in one step and the rope stops you dead, and higher values leave
    /// part of it for the next step, which comes out as a rope that hauls you back over a few
    /// tenths of a second. The rigid version is the honest Chained Together feel; the elastic
    /// one is what makes a cannon survivable.
    ///
    /// Who gives way is decided by speed, through ShareAgainst. Without that a standing player
    /// anchors a launched one, since both ends correct the same half regardless of what they
    /// are doing, and no boost pad in the game can pull a chain that is nailed down.
    ///
    /// Every link is measured from where we were at the start of the step and the corrections
    /// are added up, rather than solved one after another. Solving them in sequence - the usual
    /// way with several constraints - would re-measure each link after the previous one moved
    /// us and correct the same overshoot again and again: with two ends each doing that, the
    /// chain overshoots inwards and oscillates. Adding them is also the right answer physically
    /// for the middle of a chain, where two links pulling opposite ways should partly cancel.
    ///
    /// Two things keep it from exploding: the slack margin, so network interpolation jitter
    /// stays below the threshold, and the per-step correction cap, so a lag spike is a hard
    /// pull rather than a catapult.
    /// </summary>
    internal sealed class ChainSolver
    {
        /// <summary>Below this the direction to a neighbour is noise, and normalising it is a divide by zero.</summary>
        private const float MinSeparation = 0.001f;

        /// <summary>Combined speed below which neither of us counts as the one doing the pulling.</summary>
        private const float MinPullSpeed = 0.5f;

        private struct Neighbour
        {
            public Vector3 Position;
            public float Speed;
        }

        private readonly Neighbour[] neighbours = new Neighbour[2];

        private int neighbourCount;

        /// <summary>How tight the tightest link is, as a fraction of the chain length. Over 1 means pulling.</summary>
        public float Tension { get; private set; }

        /// <summary>Metres to the furthest neighbour, for the HUD.</summary>
        public float LongestLink { get; private set; }

        public void FixedTick(ChainedSettings settings, ChainTopology topology, NeighbourMotion motion,
            ChainLaunch launch)
        {
            Tension = 0f;
            LongestLink = 0f;

            if (!settings.Value(settings.Enabled) || !topology.Ready)
            {
                return;
            }

            // Respawns, pause menus and the end of a run all move the player around on the
            // game's terms. Pulling at the same time only produces a fight nobody wins.
            //
            // A launch is the same situation for a different reason: a pad or a cannon throws
            // harder than the rope can pull, so the rope would only read the lag between two
            // players as distance and haul back whoever it thinks is ahead.
            if (GameRefs.IsBusy || launch.Suspended)
            {
                return;
            }

            try
            {
                Solve(settings, topology, motion);
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Chain solver failed: {ex.Message}");
            }
        }

        private void Solve(ChainedSettings settings, ChainTopology topology, NeighbourMotion motion)
        {
            PlayerRef local = GameRefs.LocalPlayer;
            Rigidbody rb = local?.Rb;

            if (rb == null || rb.isKinematic)
            {
                return;
            }

            CollectNeighbours(topology, motion);

            if (neighbourCount == 0)
            {
                return;
            }

            float length = Mathf.Max(0.5f, settings.Value(settings.ChainLength));
            float limit = length * (1f + settings.Value(settings.Slack));
            float share = settings.Value(settings.Share);
            float maxCorrection = settings.Value(settings.MaxCorrection);

            // How much of the stretch is left uncorrected this step, and how much of the
            // braking goes with it: an elastic rope that still stopped you dead would be a
            // rigid one that merely looks stretched.
            float give = 1f - settings.Value(settings.Elasticity);
            float damping = settings.Value(settings.Damping) * give;

            Vector3 start = rb.position;
            Vector3 velocity = rb.linearVelocity;
            Vector3 correction = Vector3.zero;
            float ourSpeed = velocity.magnitude;

            MeasureTension(start, length);

            for (int i = 0; i < neighbourCount; i++)
            {
                Vector3 toNeighbour = neighbours[i].Position - start;
                float distance = toNeighbour.magnitude;

                if (distance <= limit || distance < MinSeparation)
                {
                    continue;
                }

                Vector3 direction = toNeighbour / distance;
                float ourShare = ShareAgainst(share, ourSpeed, neighbours[i].Speed, settings.Value(settings.SpeedPull));

                // Pulled back to the nominal length, not to the slack limit: stopping at the
                // limit would leave the link sitting exactly on its threshold, flickering in
                // and out of tension on every step.
                correction += direction * ((distance - length) * ourShare * give);

                // Braking: only the part of the velocity still moving away counts, so a jump
                // along the chain is left alone. Scaled by the same share, or the faster player
                // would be pulled back into line by the brake after winning on position.
                float away = Vector3.Dot(velocity, -direction);
                if (away > 0f)
                {
                    velocity += direction * (away * damping * Mathf.Clamp01(ourShare / Mathf.Max(share, 0.01f)));
                }
            }

            float corrected = correction.magnitude;

            if (corrected < MinSeparation)
            {
                return;
            }

            if (corrected > maxCorrection)
            {
                correction *= maxCorrection / corrected;
            }

            // MovePosition rather than assigning position: it goes through the physics step,
            // so interpolation and collision are the game's problem and not ours.
            rb.MovePosition(start + correction);
            rb.linearVelocity = velocity;
        }

        /// <summary>
        /// How much of the overshoot we give way on, once speed has had its say.
        ///
        /// Only our own body is ours to move, so winning a tug of war means correcting less
        /// than the other end does. Both machines run this, each measuring the same two speeds,
        /// so the faster player quietly stops yielding and the slower one takes up the whole
        /// correction - which is what lets a cannon or a boost pad carry the chain along
        /// instead of being anchored by whoever is standing still.
        /// </summary>
        private static float ShareAgainst(float share, float ourSpeed, float theirSpeed, float influence)
        {
            float total = ourSpeed + theirSpeed;

            // Both of us near enough still: there is nobody to lose the tug of war, and the
            // ratio would be noise divided by noise.
            if (influence <= 0f || total < MinPullSpeed)
            {
                return share;
            }

            // 0.5 when we are matched, towards 1 when only they are moving, towards 0 when only
            // we are. Doubled so a match leaves the share exactly where it was.
            float theirs = theirSpeed / total;

            return Mathf.Clamp(share * Mathf.Lerp(1f, theirs * 2f, influence), 0f, 1f);
        }

        /// <summary>Reads where our neighbours are and how fast. Ones that cannot be located are skipped.</summary>
        private void CollectNeighbours(ChainTopology topology, NeighbourMotion motion)
        {
            neighbourCount = 0;

            Add(topology.Previous, motion);
            Add(topology.Next, motion);
        }

        private void Add(int ownerId, NeighbourMotion motion)
        {
            if (ownerId < 0 || neighbourCount >= neighbours.Length)
            {
                return;
            }

            if (!motion.TryGetPosition(ownerId, out Vector3 position))
            {
                return;
            }

            neighbours[neighbourCount] = new Neighbour
            {
                Position = position,
                Speed = motion.VelocityOf(ownerId).magnitude,
            };

            neighbourCount++;
        }

        private void MeasureTension(Vector3 position, float length)
        {
            for (int i = 0; i < neighbourCount; i++)
            {
                float distance = Vector3.Distance(position, neighbours[i].Position);

                if (distance > LongestLink)
                {
                    LongestLink = distance;
                }
            }

            Tension = LongestLink / length;
        }
    }
}
