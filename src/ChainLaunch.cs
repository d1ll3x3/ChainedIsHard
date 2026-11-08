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
    /// Gets the chain out of the way of boost pads and cannons, and lets the rest of the chain
    /// come along for the ride.
    ///
    /// These are the one thing the rope cannot survive. They fire far harder than any
    /// correction is allowed to pull, and no two players cross one at the same instant when
    /// what each sees of the other is a couple of hundred milliseconds old. The rope then does
    /// the worst possible thing: it reads that lag as distance and hauls back whoever it thinks
    /// is ahead, so nobody lands where the level expects.
    ///
    /// So during a launch there is no rope, and with ShareLaunches / ShareCannons whoever was
    /// left behind gets the same kick from where they stand. The kick is the game's own, step
    /// for step: a pad sets the velocity and hands out a quarter second of air control, a
    /// cannon sets the velocity, blocks air control for as long as its data says and marks the
    /// jump as used. Anything less and the shared arc falls short of the real one.
    ///
    /// Pads are heard through LaunchEvents, which reads the game's own messages, so a
    /// neighbour's crossing reaches us whether or not we saw them touch it. Positions are only
    /// watched as a fallback, on a build where those hooks would not go in.
    ///
    /// An earlier version teleported you into a cannon a neighbour was waiting in. It is gone:
    /// being dragged into a cannon you did not choose to enter is worse than missing it.
    /// </summary>
    internal sealed class ChainLaunch
    {
        /// <summary>How often the scene is swept for pads and cannons.</summary>
        private const float RescanInterval = 5f;

        /// <summary>Metres of slop around a pad's collider, since a body touches it before its pivot does.</summary>
        private const float PadMargin = 1.2f;

        /// <summary>Seconds before the same pad can throw us again, and how long our own launch keeps a shared one away.</summary>
        private const float Cooldown = 1.5f;

        /// <summary>Seconds of air control a pad hands out. Both pad kinds pass 0.25 to ActivateAirControl.</summary>
        private const float PadAirControl = 0.25f;

        /// <summary>Seconds after which a flight that never lands is assumed to be over.</summary>
        private const float MaxFlight = 30f;

        /// <summary>Who was inside a cannon on the last step its timer was running.</summary>
        private struct Loaded
        {
            public bool Us;
            public bool Others;
        }

        private readonly List<SceneBoostPad> scenePads = new();
        private readonly List<NetworkedInteractableBoostPad> networkedPads = new();
        private readonly List<NetworkedInteractableCannon> cannons = new();

        private readonly Dictionary<int, float> cooldowns = new();

        /// <summary>Cannons counting down with one of us inside, so the shot can be noticed.</summary>
        private readonly Dictionary<int, Loaded> armed = new();

        private float nextScanAt;
        private int scannedScene;
        private bool unsupported;
        private int lastLoggedPads = -1;
        private int lastLoggedCannons = -1;
        private float suspendedUntil = float.NegativeInfinity;
        private float lastSharedAt = float.NegativeInfinity;
        private float lastSelfLaunchAt = float.NegativeInfinity;
        private float flyingSince = float.NaN;
        private float touchedDownAt = float.NaN;
        private bool airborne;

        /// <summary>True while a launch is in progress and the rope has to stay out of it.</summary>
        public bool Suspended => InFlight || Time.fixedTime < suspendedUntil;

        /// <summary>True from the moment a launch throws us until a moment after we hit something.</summary>
        public bool InFlight => !float.IsNaN(flyingSince);

        /// <summary>Seconds left on the cannon one of us is waiting in, or 0 when there is none.</summary>
        public float CannonCountdown { get; private set; }

        /// <summary>True just after we took a neighbour's launch, for the HUD.</summary>
        public bool JustShared => Time.fixedTime - lastSharedAt < 1f;

        /// <summary>What the last shared launch was, for the HUD.</summary>
        public string LastShared { get; private set; } = string.Empty;

        private bool LaunchedOurselvesRecently => Time.fixedTime - lastSelfLaunchAt < Cooldown;

        public void FixedTick(ChainedSettings settings, ChainTopology topology, NeighbourMotion motion)
        {
            CannonCountdown = 0f;

            if (unsupported || !settings.Value(settings.Enabled) || !topology.Ready)
            {
                // Whatever went off while there was no chain is not ours to act on later.
                LaunchEvents.Clear();
                return;
            }

            try
            {
                Scan();
                WatchCannons(settings, topology, motion);
                TakePadEvents(settings, topology);

                if (!LaunchEvents.PadsHooked)
                {
                    WatchPads(settings, topology, motion);
                }

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
            LaunchEvents.Clear();
            nextScanAt = 0f;
            scannedScene = 0;
            suspendedUntil = float.NegativeInfinity;
            lastSelfLaunchAt = float.NegativeInfinity;
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
        /// once: whoever pressed the button, everybody sees the same countdown, and everybody sees
        /// it run out on the same step give or take the lag. That makes it the right thing to hang
        /// both the suspension and the shared shot on.
        ///
        /// The shot is the timer running out with one of us still inside, not merely nobody being
        /// inside any more: climbing back out before it fires is not a launch.
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
                bool running = IsTimerRunning(cannon);
                bool us = running && IsLocalInside(cannon, collider);
                bool others = running && AnyoneInside(topology, motion, collider.bounds, skipLocal: true);

                if (us || others)
                {
                    // Waiting inside it. The rope is off already, so nobody is dragged out of
                    // the barrel while the count runs.
                    armed[id] = new Loaded { Us = us, Others = others };
                    Suspend(settings);

                    float remaining = Remaining(cannon);

                    if (remaining > CannonCountdown)
                    {
                        CannonCountdown = remaining;
                    }

                    continue;
                }

                if (!armed.TryGetValue(id, out Loaded loaded))
                {
                    continue;
                }

                armed.Remove(id);

                if (running)
                {
                    // Everyone climbed back out before it went off.
                    continue;
                }

                // The count ran out with one of us in there: that is the shot.
                Launched(settings);

                if (loaded.Us)
                {
                    // The game fires us itself.
                    lastSelfLaunchAt = Time.fixedTime;
                }
                else if (settings.Value(settings.ShareCannons) && !LaunchedOurselvesRecently && !OnCooldown(id) &&
                         InRange(settings, collider.bounds.center))
                {
                    ShareCannonShot(cannon, id);
                }
            }
        }

        /// <summary>
        /// Acts on every pad anyone has crossed since the last step.
        ///
        /// Anyone in the chain crossing one slackens the rope for everybody - the one who crossed
        /// it needs it out of the way as much as the one who did not. And when it was a neighbour
        /// and not us, we get the same impulse and fly the same arc from where we are standing.
        /// </summary>
        private void TakePadEvents(ChainedSettings settings, ChainTopology topology)
        {
            while (LaunchEvents.TryTake(out PadUse use))
            {
                if (use.OwnerId == topology.LocalOwnerId)
                {
                    // Our own crossing, or the lobby echoing it back to us: the game launched us.
                    lastSelfLaunchAt = Time.fixedTime;
                    Launched(settings);
                    continue;
                }

                if (!topology.Contains(use.OwnerId))
                {
                    // Somebody who is not chained to anyone.
                    continue;
                }

                Launched(settings);

                if (settings.Value(settings.ShareLaunches) && !LaunchedOurselvesRecently && !OnCooldown(use.PadId) &&
                    InRange(settings, use.Position))
                {
                    SharePadLaunch(use.Direction, use.Force, use.PadId);
                }
            }
        }

        /// <summary>
        /// The fallback for a build where the pad hooks did not go in: watches the pads anyone in
        /// the chain is inside of. It misses fast crossings - see LaunchEvents for why - but it is
        /// better than nothing.
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
                        () => pad.FindShootDirection(), () => LaunchEvents.ReadForce(pad));
                }
            }

            for (int i = 0; i < networkedPads.Count; i++)
            {
                NetworkedInteractableBoostPad pad = networkedPads[i];

                if (pad != null)
                {
                    Cross(settings, topology, motion, here, pad.gameObject, pad.GetInstanceID(),
                        () => pad.FindShootDirection(), () => LaunchEvents.ReadForce(pad));
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

            // Expand grows the size, so each side gets half of it.
            bounds.Expand(PadMargin * 2f);

            bool weAreOnIt = bounds.Contains(here);
            bool theyAreOnIt = AnyoneInside(topology, motion, bounds, skipLocal: true);

            if (!weAreOnIt && !theyAreOnIt)
            {
                return;
            }

            Launched(settings);

            // Standing on it ourselves means the game is about to launch us properly, and doing
            // both would double the kick.
            if (weAreOnIt || !settings.Value(settings.ShareLaunches) || OnCooldown(id) ||
                !InRange(settings, bounds.center))
            {
                return;
            }

            SharePadLaunch(direction(), force(), id);
        }

        /// <summary>A pad's kick, exactly as SceneBoostPad.OnPlayerEntered gives it.</summary>
        private void SharePadLaunch(Vector3 direction, float force, int sourceId)
        {
            if (direction.sqrMagnitude < 0.001f || force <= 0f || !Throw(direction.normalized * force, sourceId))
            {
                return;
            }

            try
            {
                // Without it the first quarter second of the flight cannot be steered, and the
                // real pad gives it - which is half of why a shared arc used to fall short.
                GameRefs.LocalPlayer?.Movement?.ActivateAirControl(PadAirControl);
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogWarning($"Could not hand out the pad's air control: {ex.Message}");
            }

            Shared($"pad, {force:0.#} m/s");
        }

        /// <summary>A cannon's shot, exactly as its launch routine fires the player inside.</summary>
        private void ShareCannonShot(NetworkedInteractableCannon cannon, int sourceId)
        {
            NetworkDataCannon data = ReadCannonData(cannon);

            if (data == null || data.launchForce <= 0f)
            {
                return;
            }

            // Not normalised: the game multiplies the barrel direction straight by the force.
            if (!Throw(cannon.FindShootDirection() * data.launchForce, sourceId))
            {
                return;
            }

            try
            {
                PlayerRef local = GameRefs.LocalPlayer;
                local?.Movement?.BlockAirControl(data.blockAirControlDuration);
                local?.MovementJump?.FakeJump();
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogWarning($"Could not apply the rest of the cannon shot: {ex.Message}");
            }

            Shared($"cannon, {data.launchForce:0.#} m/s");
        }

        /// <summary>Sets our velocity for a shared launch. False when there is no body to throw.</summary>
        private bool Throw(Vector3 velocity, int sourceId)
        {
            Rigidbody rb = GameRefs.LocalPlayer?.Rb;

            if (rb == null || rb.isKinematic)
            {
                return false;
            }

            rb.linearVelocity = velocity;
            rb.angularVelocity = Vector3.zero;

            cooldowns[sourceId] = Time.fixedTime + Cooldown;
            return true;
        }

        private void Shared(string what)
        {
            lastSharedAt = Time.fixedTime;
            LastShared = what;
            ChainedPlugin.Logger.LogInfo($"Took a neighbour's launch: {what}.");
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
                // If it never lifts us at all - a pad on cooldown, a neighbour out of range -
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

        /// <summary>Whether we stand close enough to where it went off for the same arc to mean anything.</summary>
        private static bool InRange(ChainedSettings settings, Vector3 source) =>
            ChainTopology.TryGetPosition(GameRefs.LocalPlayer, out Vector3 here) &&
            Vector3.Distance(here, source) <= settings.Value(settings.LaunchRange);

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

        /// <summary>
        /// Whether the cannon has us loaded. The cannon knows exactly - it is what decides whether
        /// it fires us - so it is asked first, and our position is only the fallback.
        /// </summary>
        private static bool IsLocalInside(NetworkedInteractableCannon cannon, Collider collider)
        {
            try
            {
                return cannon.IsLocalPlayerInsideCannon;
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogWarning($"Could not ask a cannon whether we are in it: {ex.Message}");
                return ChainTopology.TryGetPosition(GameRefs.LocalPlayer, out Vector3 here) &&
                       collider.bounds.Contains(here);
            }
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

        private static NetworkDataCannon ReadCannonData(NetworkedInteractableCannon cannon)
        {
            try
            {
                return cannon.dataSyncVar != null && cannon.dataSyncVar.Value != null
                    ? cannon.dataSyncVar.Value
                    : cannon.dataEditor;
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogWarning($"Could not read a cannon's force: {ex.Message}");
                return null;
            }
        }

        private bool OnCooldown(int instanceId) =>
            cooldowns.TryGetValue(instanceId, out float until) && Time.fixedTime < until;

        /// <summary>The colliders of an object and its children, as one box.</summary>
        internal static Bounds ColliderBounds(GameObject root)
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
