using System;
using System.Collections.Generic;
using System.Reflection;
using EHS;
using EHS.Interactables.BoostPad;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChainedIsHard
{
    /// <summary>A boost pad going off under somebody, as every machine hears about it.</summary>
    internal readonly struct PadUse
    {
        public PadUse(int ownerId, int padId, Vector3 position, Vector3 direction, float force)
        {
            OwnerId = ownerId;
            PadId = padId;
            Position = position;
            Direction = direction;
            Force = force;
        }

        /// <summary>Owner id of whoever crossed it.</summary>
        public int OwnerId { get; }

        public int PadId { get; }

        public Vector3 Position { get; }

        public Vector3 Direction { get; }

        public float Force { get; }
    }

    /// <summary>
    /// Hears about every boost pad anyone in the lobby crosses, from the game itself.
    ///
    /// Watching a neighbour's position for the moment they enter a pad never worked: what we
    /// see of them is interpolated and a couple of hundred milliseconds old, the pad throws them
    /// out of its volume within a step or two, and a flat pad is thinner than the distance from
    /// their feet to the point we read. Most crossings were simply never seen, which is why
    /// shared pads did nothing.
    ///
    /// The game already tells everyone, though. A pad only fires for the local player, and then
    /// sends a message so the other machines can play its effect: a scene pad goes through
    /// PlayerFeatureVisualRelay and ends in VisualSyncBoostPad.ActivateOneShot(player) on every
    /// machine, and a networked pad sends SyncObserverRpc with the client id of whoever crossed
    /// it - which is their owner id. Both name the player, and both arrive whether or not we
    /// could see them. Our own crossing is caught at the source, OnPlayerEntered, so it is known
    /// on the very step it happens rather than a round trip later.
    ///
    /// Patched one by one, so a method a game update removed only costs that patch. Every
    /// target was checked in fih-address-map.json to have a body of its own: a folded method
    /// would run the patch for its twins too, with the wrong instance. The patches only queue
    /// what happened; ChainLaunch acts on it in the physics step.
    /// </summary>
    internal static class LaunchEvents
    {
        /// <summary>What a pad is worth when its force cannot be read.</summary>
        private const float FallbackForce = 20f;

        /// <summary>Metres from its effect within which a scene pad is taken to be the one that fired.</summary>
        private const float MaxEffectDistance = 5f;

        /// <summary>More than this many undrained events means nobody is reading them; the oldest go.</summary>
        private const int MaxQueued = 32;

        private static readonly Queue<PadUse> Pending = new();

        /// <summary>True when every way a pad can fire is heard. Otherwise ChainLaunch also watches positions.</summary>
        public static bool PadsHooked { get; private set; }

        public static void Install(Harmony harmony)
        {
            int hooked =
                Patch(harmony, AccessTools.Method(typeof(SceneBoostPad), "OnPlayerEntered"),
                    nameof(OnScenePadEntered)) +
                Patch(harmony, AccessTools.Method(typeof(NetworkedInteractableBoostPad), "OnPlayerEntered"),
                    nameof(OnNetworkedPadEntered)) +
                Patch(harmony, AccessTools.Method(typeof(VisualSyncBoostPad), "ActivateOneShot"),
                    nameof(OnScenePadRelayed)) +
                Patch(harmony, ObserverRpcLogic(), nameof(OnNetworkedPadRelayed));

            PadsHooked = hooked == 4;

            if (PadsHooked)
            {
                ChainedPlugin.Logger.LogInfo("Listening for boost pads through the game's own messages.");
            }
            else
            {
                ChainedPlugin.Logger.LogWarning(
                    $"Only {hooked} of 4 boost pad hooks went in; shared pads fall back to watching positions.");
            }
        }

        /// <summary>The next pad that went off, oldest first.</summary>
        public static bool TryTake(out PadUse use)
        {
            if (Pending.Count > 0)
            {
                use = Pending.Dequeue();
                return true;
            }

            use = default;
            return false;
        }

        public static void Clear() => Pending.Clear();

        /// <summary>
        /// The networked pad's observer RPC body. FishNet suffixes it with a hash of its
        /// signature, which is not ours to rely on across builds, so it is found by prefix.
        /// </summary>
        private static MethodInfo ObserverRpcLogic()
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(NetworkedInteractableBoostPad)))
            {
                if (method.Name.StartsWith("RpcLogic___SyncObserverRpc", StringComparison.Ordinal))
                {
                    return method;
                }
            }

            return null;
        }

        private static int Patch(Harmony harmony, MethodInfo target, string postfix)
        {
            if (target == null)
            {
                ChainedPlugin.Logger.LogWarning($"No target for {postfix} in this build; not hooked.");
                return 0;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(LaunchEvents), postfix));
                return 1;
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogWarning(
                    $"Could not hook {target.DeclaringType?.Name}.{target.Name}: {ex.Message}");
                return 0;
            }
        }

        // The postfixes run inside the game's own code, so nothing may escape them.

        private static void OnScenePadEntered(SceneBoostPad __instance, PlayerRef __0)
        {
            try
            {
                Add(ChainTopology.OwnerIdOf(__0), __instance);
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Reading our own pad launch failed: {ex.Message}");
            }
        }

        private static void OnNetworkedPadEntered(NetworkedInteractableBoostPad __instance, PlayerRef __0)
        {
            try
            {
                Add(ChainTopology.OwnerIdOf(__0), __instance);
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Reading our own pad launch failed: {ex.Message}");
            }
        }

        private static void OnScenePadRelayed(VisualSyncBoostPad __instance, PlayerRef __0)
        {
            try
            {
                SceneBoostPad pad = PadBehind(__instance);

                if (pad == null)
                {
                    ChainedPlugin.Logger.LogWarning("Heard a pad go off but could not find which one.");
                    return;
                }

                Add(ChainTopology.OwnerIdOf(__0), pad);
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Reading a relayed pad launch failed: {ex.Message}");
            }
        }

        /// <summary>The RPC's only argument is the client id of the player who crossed the pad.</summary>
        private static void OnNetworkedPadRelayed(NetworkedInteractableBoostPad __instance, int __0)
        {
            try
            {
                Add(__0, __instance);
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Reading a relayed pad launch failed: {ex.Message}");
            }
        }

        private static void Add(int ownerId, SceneBoostPad pad)
        {
            if (ownerId < 0 || pad == null)
            {
                return;
            }

            Enqueue(new PadUse(ownerId, pad.GetInstanceID(), pad.transform.position, pad.FindShootDirection(),
                ReadForce(pad)));
        }

        private static void Add(int ownerId, NetworkedInteractableBoostPad pad)
        {
            if (ownerId < 0 || pad == null)
            {
                return;
            }

            Enqueue(new PadUse(ownerId, pad.GetInstanceID(), pad.transform.position, pad.FindShootDirection(),
                ReadForce(pad)));
        }

        private static void Enqueue(PadUse use)
        {
            while (Pending.Count >= MaxQueued)
            {
                Pending.Dequeue();
            }

            Pending.Enqueue(use);
        }

        /// <summary>
        /// The scene pad a relayed effect belongs to. Both carry the same FeatureUniqueID, which
        /// is what the relay looked the effect up by; the nearest pad is the fallback for a level
        /// that wired them differently.
        /// </summary>
        private static SceneBoostPad PadBehind(VisualSyncBoostPad visual)
        {
            FeatureUniqueID id = visual.uniqueID;
            Vector3 at = visual.transform.position;

            SceneBoostPad nearest = null;
            float best = MaxEffectDistance;

            foreach (SceneBoostPad pad in Object.FindObjectsOfType<SceneBoostPad>())
            {
                if (pad == null)
                {
                    continue;
                }

                FeatureUniqueID padId = pad.uniqueID;

                if (id != null && padId != null && padId.Pointer == id.Pointer)
                {
                    return pad;
                }

                float distance = Vector3.Distance(pad.transform.position, at);

                if (distance < best)
                {
                    best = distance;
                    nearest = pad;
                }
            }

            return nearest;
        }

        public static float ReadForce(SceneBoostPad pad)
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

        public static float ReadForce(NetworkedInteractableBoostPad pad)
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
    }
}
