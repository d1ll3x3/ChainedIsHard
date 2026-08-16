using System;
using ChainedIsHard.Menu;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ChainedIsHard
{
    /// <summary>
    /// Resident component: keeps the chain up to date, solves it and draws it.
    /// Needs the IntPtr constructor for IL2CPP interop.
    /// </summary>
    public class ChainedBehaviour : MonoBehaviour
    {
        public ChainedBehaviour(IntPtr ptr) : base(ptr) { }

        private ChainTopology topology;
        private NeighbourMotion motion;
        private ChainSolver solver;
        private ChainRescue rescue;
        private ChainLaunch launch;
        private ChainRenderer chainRenderer;
        private Overlay overlay;
        private ChainMenu menu;
        private Countdown countdown;

        private int sceneHandle;

        private void Awake()
        {
            topology = new ChainTopology();
            motion = new NeighbourMotion();
            solver = new ChainSolver();
            rescue = new ChainRescue();
            launch = new ChainLaunch();
            chainRenderer = new ChainRenderer();
            overlay = new Overlay();
            menu = new ChainMenu(ChainedPlugin.Settings);

            // Handed to the network rather than reached for statically: it owns the countdown,
            // the network only carries it.
            countdown = new Countdown();
            ChainNetwork.Countdown = countdown;
        }

        private void Update()
        {
            try
            {
                ChainedSettings settings = ChainedPlugin.Settings;

                WatchForSceneChange();

                if (!topology.Supported)
                {
                    return;
                }

                topology.Tick();
                ChainNetwork.Tick(settings);
                rescue.Tick(settings);

                if (InputReader.WasPressedThisFrame(settings.MenuKey.Value))
                {
                    menu.Toggle();
                }

                if (menu.IsOpen)
                {
                    // Chain hotkeys stay off while the menu has the keyboard, otherwise
                    // navigating it would toggle whatever the arrow keys are bound to.
                    menu.Tick();
                }
                else
                {
                    if (InputReader.WasPressedThisFrame(settings.ToggleKey.Value))
                    {
                        settings.Enabled.Value = !settings.Enabled.Value;
                        ChainedPlugin.Logger.LogInfo(
                            $"Chain {(settings.Enabled.Value ? "on" : "off")}" +
                            (NetRole.IsHost ? "." : " locally - the host decides in a lobby."));
                    }

                    // Anyone can call one, host or not: it is a shout, not a setting.
                    if (InputReader.WasPressedThisFrame(settings.CountdownKey.Value))
                    {
                        countdown.Start(settings.Value(settings.CountdownSeconds));
                    }
                }

                chainRenderer.Tick(settings, topology);
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Error in Update: {ex}");
            }
        }

        private void FixedUpdate()
        {
            try
            {
                if (!topology.Supported)
                {
                    return;
                }

                ChainedSettings settings = ChainedPlugin.Settings;

                // First, so everything below reads the same positions and speeds for this step.
                motion.Tick(topology);

                // Before the rope: a shared launch is meant to replace the pull, not fight it.
                launch.FixedTick(settings, topology, motion);

                solver.FixedTick(settings, topology, motion, launch);
                rescue.FixedTick(settings, topology, motion, launch);
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Error in FixedUpdate: {ex}");
            }
        }

        private void OnGUI()
        {
            try
            {
                if (menu.IsOpen)
                {
                    menu.Draw();
                }

                overlay.Draw(ChainedPlugin.Settings, topology, solver, rescue, launch, countdown);
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Error drawing the HUD: {ex}");
            }
        }

        /// <summary>
        /// A new scene means new player objects and a chain that has to be found again. The
        /// drawn links go with it: their renderers survive the load and would be left hanging
        /// between where the players used to be.
        /// </summary>
        private void WatchForSceneChange()
        {
            int handle = SceneManager.GetActiveScene().handle;

            if (handle == sceneHandle)
            {
                return;
            }

            sceneHandle = handle;

            topology.Clear();
            motion.Clear();
            launch.Reset();
            countdown.Clear();
            chainRenderer.Clear();
            ChainNetwork.Reset();
            NetRole.Invalidate();
        }

        private void OnDestroy()
        {
            try
            {
                menu?.Close();
                chainRenderer?.Clear();
                ChainedPlugin.Settings?.Save();
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Error shutting down: {ex}");
            }
        }
    }
}
