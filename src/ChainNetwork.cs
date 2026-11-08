using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using EHS;
using FishNet.Object.Synchronizing;
using FishNet.Serializing;
using HarmonyLib;
using UnityEngine;

namespace ChainedIsHard
{
    /// <summary>
    /// Carries the host's settings to the lobby, and anyone's countdown to everyone.
    ///
    /// The chain order itself never travels: it is derived from owner ids, which everyone
    /// already agrees on. What has to be shared is the tuning - two ends of one rope pulling
    /// towards different lengths is not a rope - and that is the host's to decide.
    ///
    /// There is no way to add a channel of our own. FishNet generates a serializer per
    /// broadcast type at compile time, so a type declared in a mod has none, and riding on one
    /// of the game's own broadcasts dies in Il2CppInterop, which cannot build a native thunk for
    /// a delegate taking a non-blittable struct. So both ride on things the game already sends.
    ///
    /// The settings go in a SyncVar, polled: PlayerNetworked.LookDirectionSyncVar, whose only
    /// consumer in the game is a debug overlay. Only the server can write a SyncVar, which is
    /// fine for settings - only the host has any. One Vector3 is three floats, so they go one
    /// at a time, tagged:
    ///
    ///   x = ParamTag + hash20      y = value
    ///
    /// The countdown cannot go that way. A client would have to ask the server to write it, and
    /// the only request the game has for that field, RequestUpdateLookDirectionServerRpc, does
    /// nothing unless a Troll Mode player is in the lobby - which is why a client's countdown
    /// never reached anyone. It goes through PlayerFeatureVisualRelay instead: the RPC a pad uses
    /// to have its effect played everywhere takes any ushort from anyone and hands it to every
    /// machine, the caller included. A feature id is never above 0xFFFE and the game numbers
    /// them up from 1, so a block at the top of the range is ours; an id the game does not know
    /// is looked up, not found, and ignored.
    ///
    /// The host sweeps its whole list, one setting per publish, and starts again. That sweep is
    /// what makes this self-healing: a client that joined late, missed a message or had the mod
    /// reloaded is brought in line within one cycle without anyone noticing it went wrong.
    /// State, never events.
    ///
    /// The tag sits outside the range ChaosIsHard uses (1..4 &lt;&lt; 20), so the two mods ignore
    /// each other's messages rather than acting on them. They do still share the one SyncVar
    /// though: running both at once means each overwrites the other's message, and both sides
    /// sync slowly and unreliably.
    /// </summary>
    internal static class ChainNetwork
    {
        private const int TagSize = 1 << 20;
        private const int ParamTag = 5 * TagSize;

        /// <summary>First feature id that carries a countdown; the next Countdown.Window ids are ours.</summary>
        private const ushort CountdownBase = 0xF000;

        /// <summary>
        /// A setting's hash fills everything below its tag: 20 bits. Twelve, which it used to be,
        /// made two of twenty keys collide. ParamTag plus this is still well inside the range a
        /// float32 holds exactly.
        /// </summary>
        private const int PayloadMask = TagSize - 1;

        private const float PublishInterval = 0.1f;

        /// <summary>How long without hearing from a host before we fall back to our own settings.</summary>
        private const float HostTimeout = 5f;

        private static readonly Dictionary<int, float> Remote = new();
        private static readonly List<ConfigEntryBase> Synced = new();
        private static readonly Dictionary<string, int> Hashes = new();

        private static float nextPublishAt;
        private static float lastHostHeardAt = float.NegativeInfinity;
        private static int sweepIndex;
        private static bool writeChecked;
        private static bool countdownHooked;

        /// <summary>Set once by ChainedBehaviour, which owns it.</summary>
        public static Countdown Countdown { get; set; }

        /// <summary>True while a host is actually telling us what the chain is.</summary>
        public static bool FollowingHost =>
            !NetRole.IsHost && Time.unscaledTime - lastHostHeardAt <= HostTimeout;

        /// <summary>Builds the list of settings the host decides. Called once, at load.</summary>
        public static void Start(ChainedSettings settings)
        {
            Synced.Clear();
            Hashes.Clear();

            foreach (ConfigEntryBase entry in settings.Synced)
            {
                string key = entry.Definition.Key;
                int hash = HashOf(key);

                if (Hashes.ContainsValue(hash))
                {
                    ChainedPlugin.Logger.LogError(
                        $"Setting '{key}' hashes the same as another one and cannot be synced. " +
                        "Rename it.");
                    continue;
                }

                Hashes[key] = hash;
                Synced.Add(entry);
            }

            ChainedPlugin.Logger.LogInfo($"{Synced.Count} settings follow the host.");
        }

        /// <summary>
        /// Hooks the relay's observer RPC reader, where every machine receives a countdown call.
        ///
        /// Not ActivateOneShot(ushort), which looks like the place: the compiler inlined its body
        /// into the reader, so a received call never goes through it and a patch there never runs.
        /// The reader has a body of its own (checked in fih-address-map.json) and takes only a
        /// reader and an enum, so it is safe to patch. The id is the first thing it reads, so a
        /// prefix can read it first and put the position back.
        /// </summary>
        public static void Install(Harmony harmony)
        {
            try
            {
                MethodInfo target = null;

                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(PlayerFeatureVisualRelay)))
                {
                    if (method.Name.StartsWith("RpcReader___Observers_ActivateOneShotObserverRpc", StringComparison.Ordinal))
                    {
                        target = method;
                        break;
                    }
                }

                if (target == null)
                {
                    ChainedPlugin.Logger.LogWarning("The relay's one-shot reader is not in this build; " +
                                                    "countdowns stay on the screen of whoever calls them.");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(typeof(ChainNetwork), nameof(OnOneShotReceived)));
                countdownHooked = true;
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogWarning($"Could not hook the countdown channel: {ex.Message}");
            }
        }

        /// <summary>Sends a countdown call to everyone, us included.</summary>
        public static void SendCountdown(int calledAt)
        {
            if (!countdownHooked)
            {
                return;
            }

            try
            {
                PlayerFeatureVisualRelay relay = GameRefs.LocalPlayer?.PlayerFeatureVisualRelay;

                if (relay == null)
                {
                    ChainedPlugin.Logger.LogWarning("No relay to send the countdown through; it stays on this screen.");
                    return;
                }

                relay.ActivateOneShotServerRpc((ushort)(CountdownBase + calledAt));
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Could not send the countdown: {ex.Message}");
            }
        }

        /// <summary>
        /// Peeks at the id a relay one-shot carries before the game reads it. Runs inside the
        /// game's RPC handling, so nothing may escape it and the reader must be left exactly
        /// where it was.
        /// </summary>
        private static void OnOneShotReceived(PlayerFeatureVisualRelay __instance, PooledReader __0)
        {
            ushort id;

            try
            {
                int position = __0.Position;

                try
                {
                    id = __0.ReadUInt16();
                }
                finally
                {
                    __0.Position = position;
                }
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Could not read a relayed one-shot: {ex.Message}");
                return;
            }

            if (id < CountdownBase || id >= CountdownBase + Countdown.Window)
            {
                return;
            }

            try
            {
                // Our own call coming back: it has been running here since the key went down.
                if (__instance.IsOwner)
                {
                    return;
                }

                ChainedSettings settings = ChainedPlugin.Settings;
                Countdown?.Heard(id - CountdownBase, settings.Value(settings.CountdownSeconds));
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Could not read a countdown call: {ex.Message}");
            }
        }

        public static void Tick(ChainedSettings settings)
        {
            bool host = NetRole.IsHost;

            if (host)
            {
                // Nothing on top of our own settings while we are the one deciding them.
                Remote.Clear();
            }

            // Read every frame rather than on a timer: the host cycles through a long list and a
            // client sampling on its own schedule would miss most of it.
            if (!host)
            {
                Consume();
            }

            if (!host && Remote.Count > 0 && Time.unscaledTime - lastHostHeardAt > HostTimeout)
            {
                Remote.Clear();
            }

            if (host && Time.unscaledTime >= nextPublishAt)
            {
                nextPublishAt = Time.unscaledTime + PublishInterval;
                Publish(settings);
            }
        }

        /// <summary>The host's value for a setting, when there is one.</summary>
        public static bool TryGetRemote(string key, out float value)
        {
            value = 0f;
            return FollowingHost && Hashes.TryGetValue(key, out int hash) && Remote.TryGetValue(hash, out value);
        }

        /// <summary>Dropped on a scene change; the host republishes within a tick.</summary>
        public static void Reset()
        {
            lastHostHeardAt = float.NegativeInfinity;
            Remote.Clear();
        }

        private static void Publish(ChainedSettings settings)
        {
            PlayerNetworked networked = GameRefs.LocalPlayer?.PlayerNetworked;

            if (networked?.LookDirectionSyncVar == null)
            {
                return;
            }

            Vector3? state = NextParam(settings);

            if (state == null)
            {
                return;
            }

            try
            {
                networked.LookDirectionSyncVar.Value = state.Value;
                VerifyWrite(networked, state.Value);
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Could not publish: {ex.Message}");
            }
        }

        /// <summary>The next setting in the sweep, or null when there is nothing to sync.</summary>
        private static Vector3? NextParam(ChainedSettings settings)
        {
            if (Synced.Count == 0)
            {
                return null;
            }

            sweepIndex = (sweepIndex + 1) % Synced.Count;
            ConfigEntryBase entry = Synced[sweepIndex];

            return new Vector3(
                ParamTag + HashOf(entry.Definition.Key),
                settings.Encode(entry),
                0f);
        }

        /// <summary>
        /// Checks the host's write actually landed, once.
        ///
        /// If FishNet refuses it, every client silently plays by its own settings - a confusing
        /// way to fail, so it gets a line in the log rather than nothing at all.
        /// </summary>
        private static void VerifyWrite(PlayerNetworked networked, Vector3 written)
        {
            if (writeChecked)
            {
                return;
            }

            writeChecked = true;

            if (networked.LookDirectionSyncVar.Value != written)
            {
                ChainedPlugin.Logger.LogWarning(
                    "Writing to LookDirectionSyncVar did not stick, so clients will not get the " +
                    "host's settings and will each use their own.");
            }
        }

        /// <summary>
        /// Reads the host's settings.
        ///
        /// Every visible player is checked rather than just one: a client has no reliable handle
        /// on which PlayerRef belongs to the host, and only the host ever writes a parameter, so
        /// wherever one comes from, it came from the host.
        /// </summary>
        private static void Consume()
        {
            try
            {
                var players = GameRefs.ConnectedPlayers;

                if (players == null)
                {
                    return;
                }

                for (int i = 0; i < players.Count; i++)
                {
                    SyncVar<Vector3> syncVar = players[i]?.PlayerNetworked?.LookDirectionSyncVar;

                    if (syncVar == null)
                    {
                        continue;
                    }

                    Vector3 candidate = syncVar.Value;

                    if (IsParam(candidate) && IsSaneValue(candidate.y))
                    {
                        Remote[Mathf.RoundToInt(candidate.x) - ParamTag] = candidate.y;
                        lastHostHeardAt = Time.unscaledTime;
                    }
                }
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Could not read the host's settings: {ex.Message}");
            }
        }

        /// <summary>Whether this is one of the host's settings rather than a look direction or another mod's message.</summary>
        private static bool IsParam(Vector3 candidate)
        {
            // An untouched SyncVar is zero and a real look direction is a unit vector, so
            // neither can reach the tag. The payload has to be a whole number in range.
            float payload = candidate.x - ParamTag;

            return payload >= 0f && payload <= PayloadMask && Mathf.Approximately(payload, Mathf.Round(payload));
        }

        private static bool IsSaneValue(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && Mathf.Abs(value) < 1e6f;

        /// <summary>
        /// A 20 bit hash of a setting's key.
        ///
        /// The key rather than its position in the list, so a build with one setting more or one
        /// fewer than the other side still agrees about the ones they share, instead of shifting
        /// every value one place along.
        /// </summary>
        private static int HashOf(string key)
        {
            unchecked
            {
                int hash = 17;

                foreach (char c in key)
                {
                    hash = hash * 31 + c;
                }

                return hash & PayloadMask;
            }
        }
    }
}
