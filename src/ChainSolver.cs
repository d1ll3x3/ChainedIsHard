using System;
using System.Collections.Generic;
using EHS;
using UnityEngine;

namespace ChainedIsHard
{
    /// <summary>
    /// The chain itself: a rope to each neighbour, pulled in FixedUpdate.
    ///
    /// Only the local rigidbody is ever touched. Remote players are not simulated here - their
    /// NetworkTransform drives them - so pushing them around locally would only fight the next
    /// packet and look like rubber banding. Both ends run this same solver against each other,
    /// each making its part of the correction, and the two parts add up to exactly one.
    ///
    /// Inside the length nothing happens at all. Past it the rope sets how fast the two of you
    /// should be closing in - more the further it is stretched (PullRate), never more than
    /// MaxClosingSpeed - and adds whatever part of the difference is ours (Firmness says how much
    /// of it per step). It only ever adds closing speed: a rope cannot push, so already closing in
    /// fast enough is left alone.
    ///
    /// It works on velocity and never on position. An earlier version placed the body back inside
    /// the length every step: it held, but it came out as a string of small teleports, threw away
    /// momentum and could put you through a floor.
    ///
    /// And it aims at a speed rather than applying a force. The version before this one was a
    /// spring, and a spring between two players who each see the other 100-200 ms late feeds
    /// itself: every pull lands a little out of phase, both ends pulled at full strength so the
    /// rope was twice as stiff as set, and in the air nothing bleeds the surplus off - the pair
    /// sped up and spun round each other until somebody hit a wall. Aiming at a closing speed
    /// stops the rope from overshooting, but it was not the whole story: the spin came from
    /// pulling towards where the neighbour was, a little ahead in the swing, so every pull
    /// pushed the swing along. The neighbour is now taken at their predicted position, and on
    /// top of both the rope may never take you past RopeSpeedCap - it can turn and slow you,
    /// not speed you up past it - so a runaway cannot start in the rope whatever the network does.
    ///
    /// When the rope comes back after being off - a launch, a respawn, a pause - it does not
    /// snap to its length from wherever everyone ended up. Each link starts as long as it is at
    /// that moment and is reeled in at ReelSpeed, and never lets out again: the group is wound
    /// back together instead of yanked. Moving closer shortens it for good, so walking back to
    /// each other finishes the job early.
    ///
    /// Who gives way is decided by speed, through Part. Without that a standing player anchors
    /// a launched one. A help beam overrides speed: whoever it is lifting leads and the other end
    /// follows - see HelpBeams.
    ///
    /// Every link is measured from the start of the step and the changes are added up, which is
    /// also the right answer for the middle of a chain, where two links pulling opposite ways
    /// should partly cancel. The total is capped (MaxPull), so a lag spike that reads as metres
    /// of stretch is a hard tug rather than a catapult.
    /// </summary>
    internal sealed class ChainSolver
    {
        /// <summary>Below this the direction to a neighbour is noise, and normalising it is a divide by zero.</summary>
        private const float MinSeparation = 0.001f;

        /// <summary>Combined speed below which neither of us counts as the one doing the pulling.</summary>
        private const float MinPullSpeed = 0.5f;

        /// <summary>Metres past the chain length below which a link no longer counts as being reeled in.</summary>
        private const float ReeledIn = 0.05f;

        /// <summary>
        /// The fastest the rope ever asks the two of you to close in, in m/s. However far it is
        /// stretched, it hauls rather than slingshots: past this the pull would fling you at your
        /// neighbour and out the other side.
        /// </summary>
        private const float MaxClosingSpeed = 8f;

        /// <summary>The fastest the rope alone can ever make you go, in m/s. Above it, it may only turn or slow you.</summary>
        private const float RopeSpeedCap = 10f;

        private struct Neighbour
        {
            public int OwnerId;
            public Vector3 Position;
            public Vector3 Velocity;
            public bool Lifted;
        }

        private readonly Neighbour[] neighbours = new Neighbour[2];

        /// <summary>How long each link is right now, while it is being wound back to the chain length.</summary>
        private readonly Dictionary<int, float> reels = new();

        private int neighbourCount;
        private float lastSolvedAt = float.NegativeInfinity;

        /// <summary>How tight the tightest link is, as a fraction of the chain length. Over 1 means pulling.</summary>
        public float Tension { get; private set; }

        /// <summary>Metres to the furthest neighbour, for the HUD.</summary>
        public float LongestLink { get; private set; }

        /// <summary>True while a link is still longer than the chain, being wound back in.</summary>
        public bool Reeling { get; private set; }

        public void FixedTick(ChainedSettings settings, ChainTopology topology, NeighbourMotion motion,
            ChainLaunch launch, HelpBeams beams)
        {
            Tension = 0f;
            LongestLink = 0f;
            Reeling = false;

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
                Solve(settings, topology, motion, beams);
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Chain solver failed: {ex.Message}");
            }
        }

        private void Solve(ChainedSettings settings, ChainTopology topology, NeighbourMotion motion, HelpBeams beams)
        {
            PlayerRef local = GameRefs.LocalPlayer;
            Rigidbody rb = local?.Rb;

            if (rb == null || rb.isKinematic)
            {
                return;
            }

            float dt = Time.fixedDeltaTime;

            // Any step the rope sat out means it is coming back now, and every link has to be
            // wound in from wherever it ended up rather than snapped to length.
            if (Time.fixedTime - lastSolvedAt > dt * 1.5f)
            {
                reels.Clear();
            }

            lastSolvedAt = Time.fixedTime;

            CollectNeighbours(topology, motion, beams);

            if (neighbourCount == 0)
            {
                return;
            }

            float length = Mathf.Max(0.5f, settings.Value(settings.ChainLength));
            float slack = settings.Value(settings.Slack);
            float speedPull = settings.Value(settings.SpeedPull);
            float pullRate = settings.Value(settings.PullRate);
            float firmness = settings.Value(settings.Firmness);
            float reelSpeed = settings.Value(settings.ReelSpeed);

            Vector3 position = rb.position;
            Vector3 velocity = rb.linearVelocity;
            Vector3 change = Vector3.zero;
            float ourSpeed = velocity.magnitude;
            bool weAreLifted = beams.IsLifted(topology.LocalOwnerId);

            MeasureTension(position, length);

            for (int i = 0; i < neighbourCount; i++)
            {
                Neighbour neighbour = neighbours[i];
                Vector3 toNeighbour = neighbour.Position - position;
                float distance = toNeighbour.magnitude;
                float reel = Reel(neighbour.OwnerId, length, distance, reelSpeed * dt);

                if (reel > length + ReeledIn)
                {
                    Reeling = true;
                }

                float limit = reel * (1f + slack);

                if (distance <= limit || distance < MinSeparation)
                {
                    continue;
                }

                float ourPart = weAreLifted == neighbour.Lifted
                    ? Part(ourSpeed, neighbour.Velocity.magnitude, speedPull)
                    : Lead(weAreLifted);

                if (ourPart <= 0f)
                {
                    continue;
                }

                Vector3 direction = toNeighbour / distance;

                // How fast we are closing in on them, and how fast the stretch says we should
                // be. Measured from the limit, so it asks for nothing the moment the rope goes
                // taut and more the further it is pulled past it.
                float closing = Vector3.Dot(velocity - neighbour.Velocity, direction);
                float wanted = Mathf.Min((distance - limit) * pullRate, MaxClosingSpeed);
                float missing = wanted - closing;

                if (missing <= 0f)
                {
                    continue;
                }

                change += direction * (missing * firmness * ourPart);
            }

            float strength = change.magnitude;

            if (strength < MinSeparation)
            {
                return;
            }

            float maxChange = settings.Value(settings.MaxPull) * dt;

            if (strength > maxChange)
            {
                change *= maxChange / strength;
            }

            // The guarantee: whatever the lag does to the readings above, the rope can turn you
            // and slow you but never take you past RopeSpeedCap. Anything faster has to come from
            // the game - a jump, a pad, gravity - so a runaway that starts in the rope cannot.
            Vector3 after = velocity + change;
            float ceiling = Mathf.Max(velocity.magnitude, RopeSpeedCap);

            if (after.sqrMagnitude > ceiling * ceiling)
            {
                after = after.normalized * ceiling;
            }

            rb.linearVelocity = after;
        }

        /// <summary>
        /// How long the link to this neighbour is this step.
        ///
        /// A link with no length yet - the rope just came back, or this neighbour just joined -
        /// starts as long as it is, so there is nothing to snap. From then on it only gets
        /// shorter: closer whenever the two of you are, and by this step's reel otherwise, down
        /// to the chain length where it stays.
        /// </summary>
        private float Reel(int ownerId, float length, float distance, float reelStep)
        {
            float reel = reels.TryGetValue(ownerId, out float current) ? current : distance;

            reel = Mathf.Min(reel, distance);
            reel = Mathf.Max(length, reel - reelStep);

            reels[ownerId] = reel;
            return reel;
        }

        /// <summary>
        /// Our part of the correction a link needs, once speed has had its say. Ours and theirs
        /// always add up to one, since both machines work out the same split from the same two
        /// speeds - an earlier version let both ends take all of it, which made the rope twice as
        /// hard as set and was half of what made it feed itself.
        ///
        /// Only our own body is ours to move, so winning a tug of war means taking less of the
        /// correction than the other end does. At SpeedPull 0 it is half each. Higher lets the
        /// faster player stop yielding while the slower one takes up the rest - which is what lets
        /// a running player drag a standing one instead of being anchored by them.
        /// </summary>
        private static float Part(float ourSpeed, float theirSpeed, float influence)
        {
            float total = ourSpeed + theirSpeed;

            // Both of us near enough still: there is nobody to lose the tug of war, and the
            // ratio would be noise divided by noise.
            if (influence <= 0f || total < MinPullSpeed)
            {
                return 0.5f;
            }

            return Mathf.Lerp(0.5f, theirSpeed / total, influence);
        }

        /// <summary>
        /// The part when exactly one end of the link is in a help beam: the lifted one makes none
        /// of the correction and the other makes all of it, so the beam carries the link instead
        /// of fighting it. Both machines see the same beams, so both pick the same end.
        /// </summary>
        private static float Lead(bool weAreLifted) => weAreLifted ? 0f : 1f;

        /// <summary>Reads where our neighbours are and how they move. Ones that cannot be located are skipped.</summary>
        private void CollectNeighbours(ChainTopology topology, NeighbourMotion motion, HelpBeams beams)
        {
            neighbourCount = 0;

            Add(topology.Previous, motion, beams);
            Add(topology.Next, motion, beams);
        }

        private void Add(int ownerId, NeighbourMotion motion, HelpBeams beams)
        {
            if (ownerId < 0 || neighbourCount >= neighbours.Length)
            {
                return;
            }

            // Where they are now, not where we see them: pulling towards a position that lags
            // behind a swing speeds the swing up - see NeighbourMotion.
            if (!motion.TryGetPredicted(ownerId, out Vector3 position))
            {
                return;
            }

            neighbours[neighbourCount] = new Neighbour
            {
                OwnerId = ownerId,
                Position = position,
                Velocity = motion.VelocityOf(ownerId),
                Lifted = beams.IsLifted(ownerId),
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
