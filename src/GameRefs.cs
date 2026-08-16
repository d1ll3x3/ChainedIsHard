using System;
using EHS;
using UnityEngine.SceneManagement;

namespace ChainedIsHard
{
    /// <summary>
    /// Resolves the game objects the mod needs.
    ///
    /// PlayerRef.LocalPlayer is deliberately never cached across frames: an IL2CPP wrapper
    /// kept over a respawn points at freed memory and crashes the process.
    /// </summary>
    internal static class GameRefs
    {
        public static PlayerRef LocalPlayer => PlayerRef.LocalPlayer;

        public static int SceneHandle => SceneManager.GetActiveScene().handle;

        /// <summary>Instance id of the current local player, or 0 when there is none.</summary>
        public static int PlayerIdentity
        {
            get
            {
                PlayerRef player = PlayerRef.LocalPlayer;
                return player == null ? 0 : player.GetInstanceID();
            }
        }

        /// <summary>
        /// True while the game is in a state where pulling the player around would fight the
        /// game's own transitions. The chain goes slack for as long as it lasts.
        ///
        /// Goes through GameCompat because not every build has all of these flags.
        /// </summary>
        public static bool IsBusy =>
            GameCompat.IsGameEnded ||
            GameCompat.IsPauseMenuShown ||
            GameCompat.IsGameFrozen ||
            GameCompat.IsBeingRespawned ||
            GameCompat.IsBeingSummoned ||
            GameCompat.IsCustomizationScreenShown;

        /// <summary>
        /// The list of players on this client. The backing field, not the IReadOnlyList
        /// property: the interop's version of that interface exposes neither Count nor an
        /// indexer.
        /// </summary>
        public static Il2CppSystem.Collections.Generic.List<PlayerRef> ConnectedPlayers
        {
            get
            {
                try
                {
                    return PlayerRef.connectedPlayersClient;
                }
                catch (Exception ex)
                {
                    ChainedPlugin.Logger.LogError($"Could not read the player list: {ex.Message}");
                    return null;
                }
            }
        }
    }
}
