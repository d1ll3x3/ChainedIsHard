using System;
using EHS;

namespace ChainedIsHard
{
    /// <summary>
    /// Whether we own the server side of the session. Only the host publishes the chain
    /// settings; everyone else follows. In an offline session FishNet still starts a local
    /// server, so offline behaves as host.
    /// </summary>
    internal static class NetRole
    {
        private const float RefreshInterval = 0.5f;

        private static bool isHost;
        private static float nextRefresh;

        public static bool IsHost
        {
            get
            {
                Refresh();
                return isHost;
            }
        }

        public static string Label =>
            IsHost ? "HOST" : ChainNetwork.FollowingHost ? "CLIENT · host's settings" : "CLIENT · local only";

        public static void Invalidate() => nextRefresh = 0f;

        private static void Refresh()
        {
            if (UnityEngine.Time.unscaledTime < nextRefresh)
            {
                return;
            }

            nextRefresh = UnityEngine.Time.unscaledTime + RefreshInterval;

            try
            {
                PlayerRef player = PlayerRef.LocalPlayer;
                if (player != null)
                {
                    isHost = player.IsServerInitialized;
                    return;
                }

                // Nothing resolved: fail safe towards local-only.
                isHost = false;
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Could not resolve the network role: {ex}");
                isHost = false;
            }
        }
    }
}
