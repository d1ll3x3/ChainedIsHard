using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChainedIsHard
{
    /// <summary>
    /// Everything the mod can be told to do, stored in ChainedIsHard.cfg next to the dll.
    ///
    /// Everything that decides how the chain behaves is the host's to set, and is published to
    /// the lobby: two ends of one rope pulling towards different lengths is not a rope. Those
    /// entries are listed in Synced and must be read through Value(), never directly, or a
    /// client would quietly play by its own rules.
    ///
    /// What is left local is what only affects this screen and this keyboard - the drawn rope,
    /// the readout, the binds. Nobody else has a stake in those.
    /// </summary>
    internal sealed class ChainedSettings
    {
        private readonly ConfigFile file;
        private readonly List<ConfigEntryBase> synced = new();

        public ConfigEntry<Key> MenuKey { get; }
        public ConfigEntry<Key> ToggleKey { get; }
        public ConfigEntry<Key> CountdownKey { get; }

        public ConfigEntry<bool> Enabled { get; }
        public ConfigEntry<float> ChainLength { get; }
        public ConfigEntry<float> Slack { get; }
        public ConfigEntry<float> PullRate { get; }
        public ConfigEntry<float> Firmness { get; }
        public ConfigEntry<float> MaxPull { get; }
        public ConfigEntry<float> ReelSpeed { get; }
        public ConfigEntry<float> SpeedPull { get; }

        public ConfigEntry<bool> ShareLaunches { get; }
        public ConfigEntry<bool> ShareCannons { get; }
        public ConfigEntry<float> LaunchRange { get; }
        public ConfigEntry<float> LaunchSuspend { get; }
        public ConfigEntry<float> LaunchGrace { get; }
        public ConfigEntry<float> CountdownSeconds { get; }
        public ConfigEntry<bool> RescueKeepsSpeed { get; }

        public ConfigEntry<bool> RescueEnabled { get; }
        public ConfigEntry<float> RescueDistance { get; }
        public ConfigEntry<float> RescueDelay { get; }
        public ConfigEntry<float> RespawnGrace { get; }

        public ConfigEntry<bool> ShowChain { get; }
        public ConfigEntry<float> ChainWidth { get; }
        public ConfigEntry<float> ChainSag { get; }
        public ConfigEntry<string> ChainColor { get; }
        public ConfigEntry<bool> ShowStatus { get; }

        /// <summary>Which defaults this file was written with, so a change of default can reach it.</summary>
        private ConfigEntry<int> ConfigVersion { get; }

        public ChainedSettings(ConfigFile configFile)
        {
            file = configFile;

            MenuKey = file.Bind("Binds", "MenuKey", Key.Insert,
                "Opens the in-game settings menu.");
            ToggleKey = file.Bind("Binds", "ToggleKey", Key.F1,
                "Turns the chain on and off. Only the host's toggle counts in a lobby.");
            CountdownKey = file.Bind("Binds", "CountdownKey", Key.Q,
                "Starts a countdown on everyone's screen. Anybody can call one: the hard part of " +
                "a cannon is not the jump, it is agreeing on when.");

            Enabled = file.Bind("Chain", "Enabled", true,
                "Whether players are chained at all. Decided by the host.");
            ChainLength = file.Bind("Chain", "ChainLength", 5f,
                "Metres of chain between two neighbours. Decided by the host.");
            Slack = file.Bind("Chain", "Slack", 0.05f,
                "Fraction of the length the chain may stretch before it starts pulling. " +
                "Some slack is what keeps the pull from fighting the network interpolation, " +
                "which shows up as jitter when two players stand still at full stretch.");
            PullRate = file.Bind("Chain", "PullRate", 6f,
                "How fast the rope closes the two of you back in for every metre it is stretched, " +
                "per second. 6 takes a metre of stretch out at 6 m/s. Higher is a tighter rope, " +
                "lower lets it stretch further before it hauls you in.");
            Firmness = file.Bind("Chain", "Firmness", 0.6f,
                "How much of the speed the rope wants it gets each physics step. 1 gets all of it " +
                "at once and feels like a steel cable; lower spreads it over a few steps and feels " +
                "like a rope with some give. It can never overshoot, whatever the value.");
            MaxPull = file.Bind("Chain", "MaxPull", 120f,
                "The hardest the rope can ever change your speed, in m/s². The cap is what keeps a " +
                "lag spike that reads as metres of stretch from launching you across the level.");
            ReelSpeed = file.Bind("Chain", "ReelSpeed", 4f,
                "Metres per second the rope is wound back in when it comes back after a launch, a " +
                "respawn or a pause. It starts as long as the gap between you and shortens to " +
                "the chain length, instead of snapping back and yanking everyone together.");
            SpeedPull = file.Bind("Chain", "SpeedPull", 0.5f,
                "How much speed decides who wins the tug of war. At 0 both ends always give way " +
                "equally, so a launched player is stopped by a standing one. Higher values let " +
                "whoever is moving faster drag the slower one instead of being anchored by them. " +
                "1 means the faster player never feels the rope at all, which makes walking away " +
                "from the group free.");

            LaunchSuspend = file.Bind("Launch", "LaunchSuspend", 3f,
                "Seconds the chain goes slack for after anyone touches a boost pad, and for as " +
                "long as anyone stands in a cannon whose timer is running, plus this much after " +
                "it fires. This is the setting that makes launches work at all: a pad throws you " +
                "harder than the rope can ever pull, so the rope reads the network lag between " +
                "you as distance and hauls back whoever it thinks is ahead. Long enough to cover " +
                "the whole arc.");
            LaunchGrace = file.Bind("Launch", "LaunchGrace", 1f,
                "Seconds after you hit something before the chain comes back. The suspension does " +
                "not run on a clock - it lasts until the flight is actually over - and this is " +
                "the bit on the end that covers the bounce, because the first surface a cannon " +
                "throws you into is rarely the one you end up on.");
            ShareLaunches = file.Bind("Launch", "ShareLaunches", true,
                "Take the same kick as a neighbour who crosses a boost pad you did not. The " +
                "direction and force are read off the pad itself, so you fly the arc it would " +
                "have given you, from wherever you are standing. Turn it off for the honest " +
                "version where you both have to hit it.");
            ShareCannons = file.Bind("Launch", "ShareCannons", true,
                "Take the same shot as a neighbour fired from a cannon you were not in: its " +
                "direction and force, from wherever you are standing, as long as you are within " +
                "LaunchRange of it. Turn it off and everybody has to get in.");
            LaunchRange = file.Bind("Launch", "LaunchRange", 12f,
                "Metres from the pad past which a shared launch does not apply. A pad's arc only " +
                "resembles theirs if you set off from roughly where they did, so this wants to " +
                "stay in the same order as the chain length rather than being generous.");
            CountdownSeconds = file.Bind("Launch", "CountdownSeconds", 3f,
                "How long the shared countdown runs when somebody calls one.");
            RescueKeepsSpeed = file.Bind("Rescue", "RescueKeepsSpeed", true,
                "Arrive at a rescue with your neighbour's speed rather than standing still. " +
                "Without it a rescue mid-flight drops you out of the air the moment you land " +
                "next to them.");

            RescueEnabled = file.Bind("Rescue", "RescueEnabled", true,
                "Teleport to your neighbour when the chain is stretched far past breaking, which " +
                "is what makes a respawn drag the group along with it. Decided by the host.");
            RescueDistance = file.Bind("Rescue", "RescueDistance", 20f,
                "Multiples of the chain length before the rescue fires. Below this the chain just " +
                "pulls, however hard.");
            RescueDelay = file.Bind("Rescue", "RescueDelay", 0f,
                "Seconds the distance has to stay past the limit. Rides out the moment a " +
                "teleporting neighbour is briefly nowhere sensible.");
            RespawnGrace = file.Bind("Rescue", "RespawnGrace", 3f,
                "Seconds after your own respawn during which you are the anchor: your neighbours " +
                "come to you and you are not dragged back. This is what makes a death pull the " +
                "whole chain down to the checkpoint instead of ping-ponging.");

            ShowChain = file.Bind("Visual", "ShowChain", true,
                "Draws the chain between every pair of players.");
            ChainWidth = file.Bind("Visual", "ChainWidth", 0.07f,
                "Thickness of the drawn chain, in metres.");
            ChainSag = file.Bind("Visual", "ChainSag", 0.4f,
                "How far the chain droops when there is slack left, as a fraction of the slack. " +
                "0 draws it as a straight line at all times.");
            ChainColor = file.Bind("Visual", "ChainColor", "#B9BEC8",
                "Chain colour, as an HTML colour. Falls back to grey when it cannot be parsed.");
            ShowStatus = file.Bind("Visual", "ShowStatus", true,
                "Shows the corner readout: network role, who you are chained to and how tight it is.");

            ConfigVersion = file.Bind("Internal", "ConfigVersion", 0,
                "Which defaults this file was written with. Not a setting; leave it alone.");

            // Everything that decides how the chain behaves. Order does not matter - the wire
            // identifies a setting by a hash of its key - but membership does: anything left out
            // of this list is a setting each player would silently have their own version of.
            synced.AddRange(new ConfigEntryBase[]
            {
                Enabled, ChainLength, Slack, PullRate, Firmness, MaxPull, ReelSpeed, SpeedPull,
                ShareLaunches, ShareCannons, LaunchRange, LaunchSuspend, LaunchGrace, CountdownSeconds,
                RescueEnabled, RescueDistance, RescueDelay, RespawnGrace, RescueKeepsSpeed,
            });
        }

        /// <summary>The settings the host decides for everybody.</summary>
        public IReadOnlyList<ConfigEntryBase> Synced => synced;

        public bool IsSynced(ConfigEntryBase entry) => synced.Contains(entry);

        /// <summary>A synced setting's value: the host's while there is one, ours otherwise.</summary>
        public float Value(ConfigEntry<float> entry) =>
            ChainNetwork.TryGetRemote(entry.Definition.Key, out float remote) ? remote : entry.Value;

        public bool Value(ConfigEntry<bool> entry) =>
            ChainNetwork.TryGetRemote(entry.Definition.Key, out float remote) ? remote >= 0.5f : entry.Value;

        /// <summary>A setting as a single float, which is all the wire can carry.</summary>
        public float Encode(ConfigEntryBase entry) => entry switch
        {
            ConfigEntry<float> f => f.Value,
            ConfigEntry<bool> b => b.Value ? 1f : 0f,
            _ => 0f,
        };

        /// <summary>What the host says a setting is, formatted for the menu. Empty when unknown.</summary>
        public string RemoteLabel(ConfigEntryBase entry)
        {
            if (!ChainNetwork.TryGetRemote(entry.Definition.Key, out float remote))
            {
                return string.Empty;
            }

            return entry is ConfigEntry<bool> ? remote >= 0.5f ? "on" : "off" : $"{remote:0.##}";
        }

        public Color ResolvedChainColor
        {
            get
            {
                if (ColorUtility.TryParseHtmlString(ChainColor.Value, out Color parsed))
                {
                    return parsed;
                }

                return new Color(0.72f, 0.75f, 0.78f);
            }
        }

        /// <summary>
        /// Pulls the values back into ranges the solver can work with. A chain of length zero
        /// or a share above one turns the pull into a catapult, and the config file is a text
        /// file anyone can edit.
        /// </summary>
        public void Validate()
        {
            Migrate();

            ChainLength.Value = Mathf.Clamp(ChainLength.Value, 1f, 200f);
            Slack.Value = Mathf.Clamp(Slack.Value, 0f, 0.5f);
            PullRate.Value = Mathf.Clamp(PullRate.Value, 0.5f, 30f);
            Firmness.Value = Mathf.Clamp(Firmness.Value, 0.05f, 1f);
            MaxPull.Value = Mathf.Clamp(MaxPull.Value, 10f, 1000f);
            ReelSpeed.Value = Mathf.Clamp(ReelSpeed.Value, 0.5f, 50f);
            SpeedPull.Value = Mathf.Clamp01(SpeedPull.Value);

            LaunchRange.Value = Mathf.Clamp(LaunchRange.Value, 2f, 500f);
            LaunchSuspend.Value = Mathf.Clamp(LaunchSuspend.Value, 0f, 20f);
            LaunchGrace.Value = Mathf.Clamp(LaunchGrace.Value, 0f, 10f);
            CountdownSeconds.Value = Mathf.Clamp(CountdownSeconds.Value, 1f, 30f);

            RescueDistance.Value = Mathf.Clamp(RescueDistance.Value, 1.5f, 50f);
            RescueDelay.Value = Mathf.Clamp(RescueDelay.Value, 0f, 10f);
            RespawnGrace.Value = Mathf.Clamp(RespawnGrace.Value, 0f, 30f);

            ChainWidth.Value = Mathf.Clamp(ChainWidth.Value, 0.005f, 1f);
            ChainSag.Value = Mathf.Clamp(ChainSag.Value, 0f, 2f);
        }

        /// <summary>
        /// Moves a file written with the old rope defaults onto the new ones.
        ///
        /// 1.0 shipped with SpeedPull 1 and no slack, from before launches turned the rope off.
        /// BepInEx never touches a value already in the file, so without this every existing
        /// install would keep them. Only values still sitting exactly on the old default move -
        /// anything someone chose stays. Settings that changed meaning got new keys instead, so
        /// an old number is never read as something it was not.
        /// </summary>
        private void Migrate()
        {
            const int Current = 2;

            if (ConfigVersion.Value >= Current)
            {
                return;
            }

            ResetIf(SpeedPull, 1f);
            ResetIf(Slack, 0f);

            ConfigVersion.Value = Current;
        }

        private static void ResetIf(ConfigEntry<float> entry, float oldDefault)
        {
            if (Mathf.Approximately(entry.Value, oldDefault))
            {
                entry.Value = (float)entry.DefaultValue;
                ChainedPlugin.Logger.LogInfo($"{entry.Definition.Key} moved to the new default, {entry.Value:0.##}.");
            }
        }

        public void Save()
        {
            try
            {
                file.Save();
            }
            catch (System.Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Could not save the settings: {ex.Message}");
            }
        }

        public string Summary() =>
            $"chain={(Enabled.Value ? "on" : "off")} length={ChainLength.Value:0.#}m " +
            $"rescue={(RescueEnabled.Value ? $"{RescueDistance.Value:0.#}x" : "off")} " +
            $"launch suspend={LaunchSuspend.Value:0.#}s";
    }
}
