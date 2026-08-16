using System;
using System.Collections.Generic;
using EHS;
using EHS.Interactables.Abstract;
using EHS.Interactables.BoostPad;
using EHS.Interactables.Cannon;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChainedIsHard
{
    /// <summary>
    /// Gets the chain out of the way of boost pads and cannons.
    ///
    /// These are the one thing the rope cannot survive. They fire far harder than any
    /// correction is allowed to pull, and no two players cross one at the same instant when
    /// what each sees of the other is a couple of hundred milliseconds old. The rope then does
    /// the worst possible thing: it reads that lag as distance and hauls back whoever it thinks
    /// is ahead, so nobody lands where the level expects.
    ///
    /// So during a launch there is no rope. Touch a pad, or stand in a cannon while its timer
    /// runs, and the chain goes slack for a few seconds - long enough for the arc to finish
    /// before anything starts pulling again. Everyone's machine sees the same pads, the same
    /// cannons and the same countdown, so everyone suspends at the same time without a word
    /// being sent.
    ///
    /// An earlier version teleported you into a cannon a neighbour was waiting in. It is gone:
    /// being dragged into a cannon you did not choose to enter is worse than missing it.
    ///
    /// Pads come in two flavours - EHS.SceneBoostPad and the networked one - and this level's
    /// are the scene kind. Both answer FindShootDirection() and carry a boostForce, so both are
    /// treated the same.
    /// </summary>
    internal sealed class ChainLaunch
    {
        /// <summary>How often the scene is swept for pads and cannons.</summary>
        private const float RescanInterval = 5f;

        /// <summary>Metres of slop around a pad's collider, since a body touches it before its pivot does.</summary>
        private const float PadMargin = 1.2f;

        /// <summary>Seconds before the same pad can throw us again.</summary>
        private const float Cooldown = 1.5f;

        /// <summary>What a pad is worth when its force cannot be read.</summary>
        private const float FallbackForce = 20f;

        /// <summary>Seconds after which a flight that never lands is assumed to be over.</summary>
        private const float MaxFlight = 30f;

        private readonly List<SceneBoostPad> scenePads = new();
        private readonly List<NetworkedInteractableBoostPad> networkedPads = new();
        private readonly List<NetworkedInteractableCannon> cannons = new();

        private readonly Dictionary<int, float> cooldowns = new();

        /// <summary>Cannons counting down with one of us inside, so the shot can be noticed.</summary>
        private readonly HashSet<int> armed = new();

        private float nextScanAt;
        private int scannedScene;
        private bool unsupported;
        private int lastLoggedPads = -1;
        private int lastLoggedCannons = -1;
        private float suspendedUntil = float.NegativeInfinity;
        private float lastLaunchAt = float.NegativeInfinity;
        private float flyingSince = float.NaN;
        private float touchedDownAt = float.NaN;
        private bool airborne;

        /// <summary>True while a launch is in progress and the rope has to stay out of it.</summary>
        public bool Suspended => InFlight || Time.fixedTime < suspendedUntil;

        /// <summary>True from the moment a launch throws us until a moment after we hit something.</summary>
        public bool InFlight => !float.IsNaN(flyingSince);

        /// <summary>Seconds left on the cannon we are waiting in, or 0 when there is none.</summary>
        public float CannonCountdown { get; private set; }

        /// <summary>True just after a shared pad launch, for the HUD.</summary>
        public bool JustLaunched => Time.fixedTime - lastLaunchAt < 1f;

        public void FixedTick(ChainedSettings settings, ChainTopology topology, NeighbourMotion motion)
        {
            CannonCountdown = 0f;

            if (unsupported || !settings.Value(settings.Enabled) || !topology.Ready)
            {
                return;
            }

            try
            {
                Scan();
                WatchCannons(settings, topology, motion);
                WatchPads(settings, topology, motion);
                WatchFlight(settings);
            }
            catch (Exception ex)
            {
                unsupported = true;
                ChainedPlugin.Logger.LogWarning(
                    $"Launch handling turned off, this build does not take it: {ex.Message}");
            }
        }

        public void Reset()
        {
            scenePads.Clear();
            networkedPads.Clear();
            cannons.Clear();
            cooldowns.Clear();
            armed.Clear();
            nextScanAt = 0f;
            scannedScene = 0;
            suspendedUntil = float.NegativeInfinity;
            EndFlight();
        }

        /// <summary>
        /// Collects the pads and cannons in the level.
        ///
        /// Kept between sweeps rather than searched every step - FindObjectsOfType walks every
        /// object in the scene - and swept again on a timer because a level streams more of them
        /// in as it goes. Inactive ones count: one that is off now is still one we cross later.
        /// </summary>
        private void Scan()
        {
            int scene = GameRefs.SceneHandle;

            if (scene == scannedScene && Time.unscaledTime < nextScanAt)
            {
                return;
            }

            if (scene != scannedScene)
            {
                cooldowns.Clear();
                armed.Clear();
            }

            scannedScene = scene;
            nextScanAt = Time.unscaledTime + RescanInterval;

            scenePads.Clear();
            networkedPads.Clear();
            cannons.Clear();

            foreach (SceneBoostPad pad in Object.FindObjectsOfType<SceneBoostPad>(true))
            {
                if (pad != null)
                {
                    scenePads.Add(pad);
                }
            }

            foreach (NetworkedInteractableBoostPad pad in Object.FindObjectsOfType<NetworkedInteractableBoostPad>(true))
            {
                if (pad != null)
                {
                    networkedPads.Add(pad);
                }
            }

            foreach (NetworkedInteractableCannon cannon in Object.FindObjectsOfType<NetworkedInteractableCannon>(true))
            {
                if (cannon != null)
                {
                    cannons.Add(cannon);
                }
            }

            int padCount = scenePads.Count + networkedPads.Count;

            if (padCount != lastLoggedPads || cannons.Count != lastLoggedCannons)
            {
                lastLoggedPads = padCount;
                lastLoggedCannons = cannons.Count;
                ChainedPlugin.Logger.LogInfo(
                    $"Level has {padCount} boost pad(s) ({scenePads.Count} scene, " +
                    $"{networkedPads.Count} networked) and {cannons.Count} cannon(s).");
            }
        }

        /// <summary>
        /// Watches the cannons anyone in the chain is standing in.
        ///
        /// The cannon's timer is a SyncStopwatch, so it is already running on every machine at
        /// once: whoever pressed the button, everybody sees the same countdown. That makes it
        /// the right thing to hang the suspension on. It is renewed every step while the timer
        /// runs, so it also covers the launch itself - the suspension outlives the countdown by
        /// its full length, which is exactly the window the arc needs.
        /// </summary>
        private void WatchCannons(ChainedSettings settings, ChainTopology topology, NeighbourMotion motion)
        {
            for (int i = 0; i < cannons.Count; i++)
            {
                NetworkedInteractableCannon cannon = cannons[i];
                Collider collider = cannon != null ? cannon.cannonCollider : null;

                if (collider == null)
                {
                    continue;
                }

                int id = cannon.GetInstanceID();
                bool loaded = IsTimerRunning(cannon) && AnyoneInside(topology, motion, collider.bounds);

                if (loaded)
                {
                    // Waiting inside it. The rope is off already, so nobody is dragged out of
                    // the barrel while the count runs.
                    armed.Add(id);
                    Suspend(settings);

                    float remaining = Remaining(cannon);

                    if (remaining > CannonCountdown)
                    {
                        CannonCountdown = remaining;
                    }
                }
                else if (armed.Remove(id))
                {
                    // The count finished with one of us in there: that is the shot.
                    Launched(settings);
                }
            }
        }

        /// <summary>
        /// Watches the pads anyone in the chain crosses.
        ///
        /// Crossing one suspends the rope for everybody who can see it happen, and - when the
        /// one who crossed it was not us - hands us the same impulse so we fly the same arc from
        /// where we are standing. The direction comes from the pad's own FindShootDirection()
        /// because some variants rotate the working component independently of their root, and
        /// anything derived from that root would be wrong on exactly those.
        /// </summary>
        private void WatchPads(ChainedSettings settings, ChainTopology topology, NeighbourMotion motion)
        {
            if (!ChainTopology.TryGetPosition(GameRefs.LocalPlayer, out Vector3 here))
            {
                return;
            }

            for (int i = 0; i < scenePads.Count; i++)
            {
                SceneBoostPad pad = scenePads[i];

                if (pad != null)
                {
                    Cross(settings, topology, motion, here, pad.gameObject, pad.GetInstanceID(),
                        () => pad.FindShootDirection(), () => ReadForce(pad));
                }
            }

            for (int i = 0; i < networkedPads.Count; i++)
            {
                NetworkedInteractableBoostPad pad = networkedPads[i];

                if (pad != null)
                {
                    Cross(settings, topology, motion, here, pad.gameObject, pad.GetInstanceID(),
                        () => pad.FindShootDirection(), () => ReadForce(pad));
                }
            }
        }

        private void Cross(ChainedSettings settings, ChainTopology topology, NeighbourMotion motion, Vector3 here,
            GameObject padObject, int id, Func<Vector3> direction, Func<float> force)
        {
            Bounds bounds = ColliderBounds(padObject);

            if (bounds.size == Vector3.zero)
            {
                return;
            }

            bounds.Expand(PadMargin);

            bool weAreOnIt = bounds.Contains(here);
            bool theyAreOnIt = AnyoneInside(topology, motion, bounds, skipLocal: true);

            if (!weAreOnIt && !theyAreOnIt)
            {
                return;
            }

            // Both of us are flying either way: the one who crossed it needs the rope out of the
            // way as much as the one who did not.
            Launched(settings);

            // Standing on it ourselves means the game is about to launch us properly, and doing
            // both would double the kick.
            if (weAreOnIt || !theyAreOnIt || !settings.Value(settings.ShareLaunches) || OnCooldown(id) ||
                Vector3.Distance(here, bounds.center) > settings.Value(settings.LaunchRange))
            {
                return;
            }

            Launch(force(), direction(), id);
        }

        /// <summary>
        /// Keeps the rope off until the flight is actually over.
        ///
        /// A fixed number of seconds was never going to be right: a cannon throws you for as
        /// long as it throws you, and the rope coming back mid-arc is exactly the thing that
        /// stops you short of where the level wants you. So the suspension is not timed off the
        /// launch at all - it lasts until we touch something, plus a moment. Cannons always aim
        /// at somewhere with a wall or a floor at the end of it, and that surface is the honest
        /// end of the flight.
        ///
        /// The timeout is there for the case that never comes: a launch into a pit with nothing
        /// to hit would otherwise leave the chain switched off for the rest of the run.
        /// </summary>
        private void WatchFlight(ChainedSettings settings)
        {
            if (!InFlight)
            {
                return;
            }

            if (Time.fixedTime - flyingSince > MaxFlight)
            {
                ChainedPlugin.Logger.LogInfo("Still flying after a long time, giving the chain back.");
                EndFlight();
                return;
            }

            if (!IsTouchingSomething())
            {
                // Still in the air, and nothing to wait for yet.
                airborne = true;
                touchedDownAt = float.NaN;
                return;
            }

            if (!airborne)
            {
                // Touching something we never left. A pad throws you from the ground, so the
                // frame you cross it you are still standing on it, and counting that as a
                // landing would end the flight before it started.
                //
                // If it never lifts us at all - a pad on cooldown, a neighbour standing on one -
                // then there was no launch, and the chain comes straight back.
                if (Time.fixedTime - flyingSince > settings.Value(settings.LaunchSuspend))
                {
                    EndFlight();
                }

                return;
            }

            if (float.IsNaN(touchedDownAt))
            {
                touchedDownAt = Time.fixedTime;
            }

            // The grace is what covers a bounce: cannons land you hard, and the first surface you
            // clip is often not the one you end up on.
            if (Time.fixedTime - touchedDownAt >= settings.Value(settings.LaunchGrace))
            {
                EndFlight();
            }
        }

        private void BeginFlight()
        {
            if (!InFlight)
            {
                flyingSince = Time.fixedTime;
                airborne = false;
            }

            touchedDownAt = float.NaN;
        }

        private void EndFlight()
        {
            flyingSince = float.NaN;
            touchedDownAt = float.NaN;
            airborne = false;
        }

        /// <summary>Whether we are resting against anything at all - ground, wall or ceiling.</summary>
        private static bool IsTouchingSomething()
        {
            try
            {
                GroundContact ground = GameRefs.LocalPlayer?.GroundContact;
                return ground != null && ground.IsTouching();
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogWarning($"Could not tell whether we have landed: {ex.Message}");

                // Better to hand the chain back than to leave it off forever.
                return true;
            }
        }

        /// <summary>Keeps the rope off for a moment. Used while a cannon is still counting down.</summary>
        private void Suspend(ChainedSettings settings) =>
            suspendedUntil = Time.fixedTime + settings.Value(settings.LaunchSuspend);

        /// <summary>
        /// Something has just thrown one of us. Keeps the rope off until we land.
        ///
        /// Separate from Suspend because waiting inside a cannon and being fired out of it are
        /// not the same thing: while you wait you are standing on something, and a flight that
        /// counted that as a landing would end before the cannon even went off.
        /// </summary>
        private void Launched(ChainedSettings settings)
        {
            Suspend(settings);
            BeginFlight();
        }

        /// <summary>Whether anyone in the chain is standing in this volume.</summary>
        private static bool AnyoneInside(ChainTopology topology, NeighbourMotion motion, Bounds bounds,
            bool skipLocal = false)
        {
            IReadOnlyList<int> order = topology.Order;

            for (int i = 0; i < order.Count; i++)
            {
                if (skipLocal && order[i] == topology.LocalOwnerId)
                {
                    continue;
                }

                if (motion.TryGetPosition(order[i], out Vector3 position) && bounds.Contains(position))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Gives us the velocity the pad would have given us, exactly as the game computes it.</summary>
        private void Launch(float force, Vector3 direction, int sourceId)
        {
            Rigidbody rb = GameRefs.LocalPlayer?.Rb;

            if (rb == null || rb.isKinematic || direction.sqrMagnitude < 0.001f || force <= 0f)
            {
                return;
            }

            rb.linearVelocity = direction.normalized * force;
            rb.angularVelocity = Vector3.zero;

            cooldowns[sourceId] = Time.fixedTime + Cooldown;
            lastLaunchAt = Time.fixedTime;

            ChainedPlugin.Logger.LogInfo($"Took the same pad launch, {force:0.#} m/s.");
        }

        private static bool IsTimerRunning(NetworkedInteractableBase interactable)
        {
            try
            {
                return interactable.isTimerActive;
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogWarning($"Could not read a cannon's timer: {ex.Message}");
                return false;
            }
        }

        /// <summary>Seconds left before the cannon fires, from the stopwatch it replicates to everyone.</summary>
        private static float Remaining(NetworkedInteractableBase interactable)
        {
            try
            {
                float elapsed = interactable.cooldownStopWatchSyncVar?.Elapsed ?? 0f;
                return Mathf.Max(0f, interactable.cooldownDuration - elapsed);
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogWarning($"Could not read a cannon's countdown: {ex.Message}");
                return 0f;
            }
        }

        private static float ReadForce(SceneBoostPad pad)
        {
            try
            {
                return pad.boostForce > 0f ? pad.boostForce : FallbackForce;
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogWarning($"Could not read a pad's force: {ex.Message}");
                return FallbackForce;
            }
        }

        private static float ReadForce(NetworkedInteractableBoostPad pad)
        {
            try
            {
                NetworkDataBoostPad data = pad.dataSyncVar != null ? pad.dataSyncVar.Value : pad.dataEditor;
                return data != null && data.boostForce > 0f ? data.boostForce : FallbackForce;
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogWarning($"Could not read a pad's force: {ex.Message}");
                return FallbackForce;
            }
        }

        private bool OnCooldown(int instanceId) =>
            cooldowns.TryGetValue(instanceId, out float until) && Time.fixedTime < until;

        /// <summary>The colliders of an object and its children, as one box.</summary>
        private static Bounds ColliderBounds(GameObject root)
        {
            var bounds = new Bounds();
            bool any = false;

            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null)
                {
                    continue;
                }

                if (!any)
                {
                    bounds = collider.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            return any ? bounds : new Bounds(root.transform.position, Vector3.zero);
        }
    }
}
