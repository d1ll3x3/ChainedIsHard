using System;
using System.Collections.Generic;
using EHS;
using EHS.Player.HelpBeam;
using FishNet.Object;
using UnityEngine;

namespace ChainedIsHard
{
    /// <summary>
    /// Who in the chain is being lifted by a help beam.
    ///
    /// A help beam is how one player lifts another: whoever stands in somebody else's beam is
    /// pushed upwards every physics step (NetworkedHelpBeam.FixedUpdate, an acceleration, never
    /// on its owner). Left alone, the rope reads that as somebody wandering off and hauls them
    /// back down, fighting the beam step after step - so the beam stalls at the chain length and
    /// shakes there, which is the opposite of help.
    ///
    /// So the lifted player leads: the solver lets them rise and makes their neighbours follow,
    /// the way a rope pulled from above takes everything below it along. That is what a beam is
    /// for in a chained run - getting the group up somewhere none of them could jump to.
    ///
    /// Both machines have to agree on who leads, or both ends yield and the link goes slack, or
    /// neither does and it fights. Our own state comes from the game (IsCurrentlyLifted is the
    /// list of beams we are in); a neighbour's comes from where we see them against the beams
    /// everybody has up, which every machine knows because the active beam is a SyncVar.
    /// </summary>
    internal sealed class HelpBeams
    {
        /// <summary>Metres of slop around a beam's volume, since what we see of a neighbour lags behind them.</summary>
        private const float Margin = 0.5f;

        private readonly HashSet<int> lifted = new();
        private readonly List<Bounds> beams = new();
        private readonly List<int> beamOwners = new();

        private bool unsupported;

        /// <summary>True when this player is being lifted by somebody's beam.</summary>
        public bool IsLifted(int ownerId) => lifted.Contains(ownerId);

        public void FixedTick(ChainedSettings settings, ChainTopology topology, NeighbourMotion motion)
        {
            lifted.Clear();

            if (unsupported || !settings.Value(settings.Enabled) || !topology.Ready)
            {
                return;
            }

            try
            {
                Collect();

                if (IsLocalLifted())
                {
                    lifted.Add(topology.LocalOwnerId);
                }

                if (beams.Count > 0)
                {
                    FindLiftedNeighbours(topology, motion);
                }
            }
            catch (Exception ex)
            {
                unsupported = true;
                lifted.Clear();
                ChainedPlugin.Logger.LogWarning($"Help beam handling turned off, this build does not take it: {ex.Message}");
            }
        }

        /// <summary>Every beam anyone has up, with who put it there.</summary>
        private void Collect()
        {
            beams.Clear();
            beamOwners.Clear();

            var players = GameRefs.ConnectedPlayers;

            if (players == null)
            {
                return;
            }

            for (int i = 0; i < players.Count; i++)
            {
                PlayerRef player = players[i];
                PlayerHelpBeamV2 beam = player != null ? player.PlayerHelpBeamV2 : null;

                if (beam == null || !beam.IsHelpBeamActive)
                {
                    continue;
                }

                NetworkObject spawned = beam.activeHelpBeamSyncVar?.Value;

                if (spawned == null)
                {
                    continue;
                }

                Bounds bounds = ChainLaunch.ColliderBounds(spawned.gameObject);

                if (bounds.size == Vector3.zero)
                {
                    continue;
                }

                bounds.Expand(Margin * 2f);
                beams.Add(bounds);
                beamOwners.Add(ChainTopology.OwnerIdOf(player));
            }
        }

        private void FindLiftedNeighbours(ChainTopology topology, NeighbourMotion motion)
        {
            IReadOnlyList<int> order = topology.Order;

            for (int i = 0; i < order.Count; i++)
            {
                int ownerId = order[i];

                if (ownerId == topology.LocalOwnerId || !motion.TryGetPosition(ownerId, out Vector3 position))
                {
                    continue;
                }

                for (int b = 0; b < beams.Count; b++)
                {
                    // A beam never lifts the one who put it up.
                    if (beamOwners[b] != ownerId && beams[b].Contains(position))
                    {
                        lifted.Add(ownerId);
                        break;
                    }
                }
            }
        }

        private static bool IsLocalLifted()
        {
            PlayerHelpBeamV2 beam = GameRefs.LocalPlayer?.PlayerHelpBeamV2;
            return beam != null && beam.IsCurrentlyLifted;
        }
    }
}
