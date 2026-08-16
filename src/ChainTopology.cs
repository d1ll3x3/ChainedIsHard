using System;
using System.Collections.Generic;
using System.Reflection;
using EHS;
using UnityEngine;

namespace ChainedIsHard
{
    /// <summary>
    /// Who is chained to whom.
    ///
    /// The chain is a line: players sorted by FishNet owner id, each linked to the next.
    /// Owner ids are handed out by the server and every machine sees the same numbers, so
    /// every machine derives the same chain from the same list without a single byte going
    /// over the wire. That is the whole reason this mod needs almost no networking.
    ///
    /// Nothing here holds on to a PlayerRef between frames: an IL2CPP wrapper kept over a
    /// respawn points at freed memory. Only the owner ids are kept, and the player is looked
    /// up again from the live list every time it is needed. With four players that lookup is
    /// four comparisons, which is cheaper than being careful about lifetimes.
    /// </summary>
    internal sealed class ChainTopology
    {
        private const float RefreshInterval = 0.25f;

        /// <summary>
        /// PlayerNetworked.OwnerId, read by reflection.
        ///
        /// It is inherited from FishNet's NetworkBehaviour rather than declared by the game,
        /// so which type the interop actually puts it on can differ between builds. A direct
        /// call would be a MissingMethodException on the first frame; this is a null check.
        /// </summary>
        private static PropertyInfo ownerIdProperty;
        private static bool ownerIdResolved;

        private readonly List<int> order = new();
        private readonly List<int> scratch = new();

        private float nextRefreshAt;
        private int localOwnerId = -1;
        private bool warnedUnsupported;

        /// <summary>Owner ids of every chained player, in chain order.</summary>
        public IReadOnlyList<int> Order => order;

        public int LocalOwnerId => localOwnerId;

        /// <summary>True when there is an actual chain to solve: us plus at least one neighbour.</summary>
        public bool Ready => localOwnerId >= 0 && order.Count >= 2 && order.Contains(localOwnerId);

        /// <summary>The neighbour before us in the chain, or -1 when we are the first link.</summary>
        public int Previous { get; private set; } = -1;

        /// <summary>The neighbour after us in the chain, or -1 when we are the last link.</summary>
        public int Next { get; private set; } = -1;

        public void Tick()
        {
            if (Time.unscaledTime < nextRefreshAt)
            {
                return;
            }

            nextRefreshAt = Time.unscaledTime + RefreshInterval;

            try
            {
                Rebuild();
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Could not rebuild the chain: {ex.Message}");
                Clear();
            }
        }

        public void Clear()
        {
            order.Clear();
            localOwnerId = -1;
            Previous = -1;
            Next = -1;
        }

        private void Rebuild()
        {
            scratch.Clear();

            var players = GameRefs.ConnectedPlayers;
            PlayerRef local = GameRefs.LocalPlayer;

            localOwnerId = local != null ? OwnerIdOf(local) : -1;

            if (players != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    int id = OwnerIdOf(players[i]);

                    // A player that has not finished spawning has no owner yet, and a
                    // duplicate would put the same person in the chain twice.
                    if (id >= 0 && !scratch.Contains(id))
                    {
                        scratch.Add(id);
                    }
                }
            }

            // The local player is not always in connectedPlayersClient on the frame it
            // spawns, and a chain missing us is worse than one built a moment early.
            if (localOwnerId >= 0 && !scratch.Contains(localOwnerId))
            {
                scratch.Add(localOwnerId);
            }

            scratch.Sort();

            if (!SameAs(scratch))
            {
                order.Clear();
                order.AddRange(scratch);
                ChainedPlugin.Logger.LogInfo(
                    $"Chain is now {string.Join(" - ", order)} (we are {localOwnerId}).");
            }

            UpdateNeighbours();
        }

        private void UpdateNeighbours()
        {
            Previous = -1;
            Next = -1;

            int index = order.IndexOf(localOwnerId);
            if (index < 0)
            {
                return;
            }

            if (index > 0)
            {
                Previous = order[index - 1];
            }

            if (index < order.Count - 1)
            {
                Next = order[index + 1];
            }
        }

        private bool SameAs(List<int> candidate)
        {
            if (candidate.Count != order.Count)
            {
                return false;
            }

            for (int i = 0; i < candidate.Count; i++)
            {
                if (candidate[i] != order[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The live PlayerRef for an owner id, or null when they are not here.</summary>
        public static PlayerRef Find(int ownerId)
        {
            if (ownerId < 0)
            {
                return null;
            }

            var players = GameRefs.ConnectedPlayers;
            if (players == null)
            {
                return null;
            }

            for (int i = 0; i < players.Count; i++)
            {
                PlayerRef player = players[i];

                if (player != null && OwnerIdOf(player) == ownerId)
                {
                    return player;
                }
            }

            PlayerRef local = GameRefs.LocalPlayer;
            return local != null && OwnerIdOf(local) == ownerId ? local : null;
        }

        /// <summary>Where a player is, from the rigidbody when there is one.</summary>
        public static bool TryGetPosition(PlayerRef player, out Vector3 position)
        {
            position = Vector3.zero;

            if (player == null)
            {
                return false;
            }

            try
            {
                Rigidbody rb = player.Rb;

                if (rb != null)
                {
                    position = rb.position;
                    return true;
                }

                Transform transform = player.transform;
                if (transform == null)
                {
                    return false;
                }

                position = transform.position;
                return true;
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Could not read a player's position: {ex.Message}");
                return false;
            }
        }

        /// <summary>The FishNet owner id of a player, or -1 when it cannot be read.</summary>
        public static int OwnerIdOf(PlayerRef player)
        {
            if (player == null)
            {
                return -1;
            }

            try
            {
                PlayerNetworked networked = player.PlayerNetworked;
                if (networked == null)
                {
                    return -1;
                }

                PropertyInfo property = ResolveOwnerId();
                if (property == null)
                {
                    return -1;
                }

                object value = property.GetValue(networked);
                return value is int id ? id : -1;
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Could not read an owner id: {ex.Message}");
                return -1;
            }
        }

        private static PropertyInfo ResolveOwnerId()
        {
            if (ownerIdResolved)
            {
                return ownerIdProperty;
            }

            ownerIdResolved = true;
            ownerIdProperty = typeof(PlayerNetworked).GetProperty("OwnerId",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

            if (ownerIdProperty == null)
            {
                ChainedPlugin.Logger.LogError(
                    "PlayerNetworked has no OwnerId in this build, so the chain order cannot be " +
                    "agreed on between machines. The mod will stay idle.");
            }

            return ownerIdProperty;
        }

        /// <summary>False when this build cannot tell us who owns a player. Nothing runs then.</summary>
        public bool Supported
        {
            get
            {
                if (ResolveOwnerId() != null)
                {
                    return true;
                }

                if (!warnedUnsupported)
                {
                    warnedUnsupported = true;
                    ChainedPlugin.Logger.LogWarning("Chain disabled: no owner ids on this build.");
                }

                return false;
            }
        }
    }
}
