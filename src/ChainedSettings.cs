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
        public ConfigEntry<float> Share { get; }
        public ConfigEntry<float> MaxCorrection { get; }
        public ConfigEntry<float> Damping { get; }
        public ConfigEntry<float> Elasticity { get; }
        public ConfigEntry<float> SpeedPull { get; }

        public ConfigEntry<bool> ShareLaunches { get; }
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
            Slack = file.Bind("Chain", "Slack", 0f,
                "Fraction of the length the chain may stretch before it starts pulling. " +
                "Some slack is what keeps the pull from fighting the network interpolation, " +
                "which shows up as jitter when two players stand still at full stretch.");
            Share = file.Bind("Chain", "Share", 1f,
                "How much of the overshoot this player corrects. Both ends run the same solver, " +
                "so 0.5 each adds up to the whole. Raise it to be dragged harder than you drag.");
            MaxCorrection = file.Bind("Chain", "MaxCorrection", 0.6f,
                "Metres this player may be pulled in a single physics step. The cap is what " +
                "keeps a lag spike from launching you across the level.");
            Damping = file.Bind("Chain", "Damping", 0.6f,
                "How much of the speed you are moving away at is cancelled when the chain goes " +
                "tight. 1 is a dead stop, 0 is a pull with no braking at all.");
            Elasticity = file.Bind("Chain", "Elasticity", 0.98f,
                "How much give the rope has. 0 is the rigid rope: the moment it runs out of " +
                "length you stop dead. Higher values leave part of the stretch uncorrected each " +
                "step, so it hauls you back over a few tenths of a second instead of instantly, " +
                "which is far kinder on cannons and boost pads where the two of you are never " +
                "quite in the same place. Above about 0.9 it barely pulls at all.");
            SpeedPull = file.Bind("Chain", "SpeedPull", 1f,
                "How much speed decides who wins the tug of war. At 0 both ends always give way " +
                "equally, so a launched player is stopped by a standing one. Higher values let " +
                "whoever is moving faster drag the slower one instead of being anchored by them, " +
                "which is what makes a cannon or a boost pad take the whole chain with it. 1 is " +
                "the strongest it gets.");

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

            // Everything that decides how the chain behaves. Order does not matter - the wire
            // identifies a setting by a hash of its key - but membership does: anything left out
            // of this list is a setting each player would silently have their own version of.
            synced.AddRange(new ConfigEntryBase[]
            {
                Enabled, ChainLength, Slack, Share, MaxCorrection, Damping, Elasticity, SpeedPull,
                ShareLaunches, LaunchRange, LaunchSuspend, LaunchGrace, CountdownSeconds,
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
            ChainLength.Value = Mathf.Clamp(ChainLength.Value, 1f, 200f);
            Slack.Value = Mathf.Clamp(Slack.Value, 0f, 0.5f);
            Share.Value = Mathf.Clamp(Share.Value, 0.05f, 1f);
            MaxCorrection.Value = Mathf.Clamp(MaxCorrection.Value, 0.02f, 5f);
            Damping.Value = Mathf.Clamp01(Damping.Value);

            // Not quite 1: a fully elastic rope never corrects anything and the chain would
            // silently stop existing.
            Elasticity.Value = Mathf.Clamp(Elasticity.Value, 0f, 0.98f);
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
