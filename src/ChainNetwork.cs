using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using EHS;
using FishNet.Object.Synchronizing;
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
    /// a delegate taking a non-blittable struct. What does work is writing into an existing
    /// SyncVar and polling it: PlayerNetworked.LookDirectionSyncVar, whose only consumer in the
    /// game is a debug overlay.
    ///
    /// One Vector3 is three floats, so the settings go one at a time, tagged:
    ///
    ///   x = ParamTag + hash12      y = value          (host only)
    ///   x = CountdownTag + token   y = seconds        (anyone)
    ///
    /// The host sweeps its whole list, one setting per publish, and starts again. That sweep is
    /// what makes this self-healing: a client that joined late, missed a message or had the mod
    /// reloaded is brought in line within one cycle without anyone noticing it went wrong.
    /// State, never events.
    ///
    /// The tags sit outside the range ChaosIsHard uses (1..4 &lt;&lt; 20), so the two mods ignore
    /// each other's messages rather than acting on them. They do still share the one SyncVar
    /// though: running both at once means each overwrites the other's message, and both sides
    /// sync slowly and unreliably.
    /// </summary>
    internal static class ChainNetwork
    {
        private const int TagSize = 1 << 20;
        private const int ParamTag = 5 * TagSize;
        private const int CountdownTag = 6 * TagSize;

        /// <summary>Hashes and tokens are 12 bits, well inside a float32's exact integer range.</summary>
        private const int PayloadMask = 0xFFF;

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

        public static void Tick(ChainedSettings settings)
        {
            bool host = NetRole.IsHost;

            if (host)
            {
                // Nothing on top of our own settings while we are the one deciding them.
                Remote.Clear();
            }

            // Read every frame rather than on a timer: the host cycles through a long list and a
            // client sampling on its own schedule would miss most of it. The countdown rides the
            // same channel and is read by host and client alike.
            Consume(host);

            if (!host && Remote.Count > 0 && Time.unscaledTime - lastHostHeardAt > HostTimeout)
            {
                Remote.Clear();
            }

            if (Time.unscaledTime >= nextPublishAt)
            {
                nextPublishAt = Time.unscaledTime + PublishInterval;
                Publish(settings, host);
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

        private static void Publish(ChainedSettings settings, bool host)
        {
            PlayerNetworked networked = GameRefs.LocalPlayer?.PlayerNetworked;

            if (networked?.LookDirectionSyncVar == null)
            {
                return;
            }

            Vector3? state = null;

            // A countdown is somebody shouting "now": it goes out ahead of the sweep, which can
            // wait a tenth of a second.
            if (Countdown != null && Countdown.NeedsPublishing)
            {
                state = new Vector3(CountdownTag + (Countdown.Token & PayloadMask), Countdown.Length, 0f);
            }
            else if (host)
            {
                state = NextParam(settings);
            }

            if (state == null)
            {
                return;
            }

            try
            {
                if (host)
                {
                    networked.LookDirectionSyncVar.Value = state.Value;
                    VerifyWrite(networked, state.Value);
                }
                else
                {
                    // A client cannot write the SyncVar: FishNet SyncVars are the server's to
                    // set, so writing one here would change nothing but this machine's copy.
                    // The game already has the round trip for it - it is how a client publishes
                    // its own look direction - and the server writing it is what replicates it
                    // to everybody. This is the only reason a client's countdown reaches anyone.
                    networked.RequestUpdateLookDirectionServerRpc(state.Value, null);
                }
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
        /// Reads everyone's messages.
        ///
        /// Every visible player is checked rather than just one: a client has no reliable handle
        /// on which PlayerRef belongs to the host, and only the host ever writes a parameter, so
        /// wherever one comes from, it came from the host.
        /// </summary>
        private static void Consume(bool host)
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
                    int tag = Tag(candidate);

                    if (tag == CountdownTag)
                    {
                        int token = Mathf.RoundToInt(candidate.x) - CountdownTag;

                        if (candidate.y > 0f && candidate.y <= 60f)
                        {
                            Countdown?.Heard(token, candidate.y);
                        }
                    }
                    else if (tag == ParamTag && !host && IsSaneValue(candidate.y))
                    {
                        Remote[Mathf.RoundToInt(candidate.x) - ParamTag] = candidate.y;
                        lastHostHeardAt = Time.unscaledTime;
                    }
                }
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Could not read the lobby's messages: {ex.Message}");
            }
        }

        /// <summary>Which kind of message this is, or 0 when it is not one of ours.</summary>
        private static int Tag(Vector3 candidate)
        {
            if (candidate.x < ParamTag || candidate.x >= CountdownTag + TagSize)
            {
                return 0;
            }

            int tag = Mathf.FloorToInt(candidate.x / TagSize) * TagSize;
            float payload = candidate.x - tag;

            // An untouched SyncVar is zero and a real look direction is a unit vector, so
            // neither can reach a tag. A payload has to be a whole number in range.
            return payload >= 0f && payload <= PayloadMask && Mathf.Approximately(payload, Mathf.Round(payload))
                ? tag
                : 0;
        }

        private static bool IsSaneValue(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && Mathf.Abs(value) < 1e6f;

        /// <summary>
        /// A 12 bit hash of a setting's key.
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
