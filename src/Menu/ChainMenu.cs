using System;
using System.Collections.Generic;
using EHS;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChainedIsHard.Menu
{
    /// <summary>
    /// In-game settings menu, driven by the arrow keys.
    ///
    /// The chain has to be tuned while playing: how long it should be depends on the level and
    /// on how many of you there are, and no length read off a config file survives contact with
    /// an actual jump. Every row edits a BepInEx ConfigEntry directly, so a value changed
    /// mid-run is already the saved value.
    ///
    /// Rows the host decides are marked, and on a client they say so rather than pretending an
    /// edit did something: the client's own value is still what it falls back to when the host
    /// goes away, so it is editable, just not in force.
    ///
    /// Drawing is labels and filled rectangles only: GUI.Button and GUI.TextField are stripped
    /// in this IL2CPP build and crash the game, and keys are read through the Input System in
    /// Update like the rest of the mod rather than from the IMGUI events.
    /// </summary>
    internal sealed class ChainMenu
    {
        private const float RowHeight = 22f;
        private const float Width = 470f;
        private const float Padding = 18f;

        private readonly ChainedSettings settings;
        private readonly List<MenuRow> rows = new();

        private int selected;
        private float upRepeat;
        private float downRepeat;
        private float leftRepeat;
        private float rightRepeat;

        private CursorLockMode savedLockState = CursorLockMode.Locked;
        private bool savedCursorVisible;
        private bool blockingGameInput;

        // Blocker token for the game's own jump block list, created on first use.
        private Il2CppSystem.Object jumpToken;

        private GUIStyle titleStyle;
        private GUIStyle labelStyle;
        private GUIStyle valueStyle;
        private GUIStyle headerStyle;
        private GUIStyle hintStyle;
        private GUIStyle remoteStyle;

        public ChainMenu(ChainedSettings settings)
        {
            this.settings = settings;
            Build();
            selected = FirstSelectable();
        }

        public bool IsOpen { get; private set; }

        public void Toggle()
        {
            IsOpen = !IsOpen;

            if (IsOpen)
            {
                // Remembered rather than assumed, so closing the menu from a screen that
                // already had a free cursor does not lock it away.
                savedLockState = Cursor.lockState;
                savedCursorVisible = Cursor.visible;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                return;
            }

            CancelCapture();
            settings.Validate();
            settings.Save();
            SetGameInputBlocked(false);
            Cursor.lockState = savedLockState;
            Cursor.visible = savedCursorVisible;
        }

        public void Tick()
        {
            if (!IsOpen)
            {
                return;
            }

            // The game grabs the cursor back every frame, so it has to be re-released.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Re-asserted every frame rather than only on open: the player can respawn into a
            // fresh instance while the menu is up, and the block lives on the instance.
            SetGameInputBlocked(true);

            if (rows[selected] is KeyRow capturing && capturing.Capturing)
            {
                capturing.Capture();
                return;
            }

            if (InputReader.WasPressedOrRepeating(Key.UpArrow, ref upRepeat))
            {
                Step(-1);
            }

            if (InputReader.WasPressedOrRepeating(Key.DownArrow, ref downRepeat))
            {
                Step(1);
            }

            // A client cannot edit what the host is publishing: the next message would overwrite
            // it a tenth of a second later, and a value that silently springs back is worse than
            // one that does not move.
            bool editable = !ChainNetwork.FollowingHost || !IsHostDecided(rows[selected]);

            if (editable && InputReader.WasPressedOrRepeating(Key.LeftArrow, ref leftRepeat))
            {
                rows[selected].Adjust(-1);
            }

            if (editable && InputReader.WasPressedOrRepeating(Key.RightArrow, ref rightRepeat))
            {
                rows[selected].Adjust(1);
            }

            if (InputReader.WasPressedThisFrame(Key.Enter) && rows[selected] is KeyRow key)
            {
                key.Adjust(0);
            }

            if (editable && InputReader.WasPressedThisFrame(Key.R))
            {
                rows[selected].Reset();
            }
        }

        public void Draw()
        {
            if (!IsOpen)
            {
                return;
            }

            EnsureStyles();

            bool following = ChainNetwork.FollowingHost;

            float height = Padding * 2f + RowHeight * (rows.Count + 3);
            float x = (Screen.width - Width) * 0.5f;
            float y = Mathf.Max(20f, (Screen.height - height) * 0.5f);

            Gui.Fill(new Rect(x, y, Width, height), Gui.Backdrop);

            float rowX = x + Padding;
            float rowWidth = Width - Padding * 2f;
            float cursorY = y + Padding;

            GUI.Label(new Rect(rowX, cursorY, rowWidth, RowHeight), "CHAINED IS HARD", titleStyle);
            cursorY += RowHeight * 1.4f;

            for (int i = 0; i < rows.Count; i++)
            {
                MenuRow row = rows[i];
                var rect = new Rect(rowX, cursorY, rowWidth, RowHeight);

                if (i == selected)
                {
                    Gui.Fill(rect, Gui.Selection);
                }

                if (!row.Selectable)
                {
                    GUI.Label(rect, row.Label, headerStyle);
                }
                else if (following && IsHostDecided(row))
                {
                    // The host's value, not ours: ours is still in the file and comes back the
                    // moment the host stops talking, but it is not what is being played.
                    GUI.Label(rect, row.Label, labelStyle);
                    GUI.Label(rect, settings.RemoteLabel(row.Entry), remoteStyle);
                }
                else
                {
                    GUI.Label(rect, row.Label, labelStyle);
                    GUI.Label(rect, row.Value, valueStyle);
                }

                cursorY += RowHeight;
            }

            cursorY += RowHeight * 0.3f;
            GUI.Label(new Rect(rowX, cursorY, rowWidth, RowHeight),
                following
                    ? $"the host is setting the chain   ·   {settings.MenuKey.Value} = close"
                    : $"arrows = move / change   ·   R = default   ·   {settings.MenuKey.Value} = close",
                hintStyle);
        }

        /// <summary>Whether this row edits one of the settings the host decides for everybody.</summary>
        private bool IsHostDecided(MenuRow row) => row.Entry != null && settings.IsSynced(row.Entry);

        /// <summary>Called on unload so a menu left open does not keep the game's input blocked.</summary>
        public void Close()
        {
            if (IsOpen)
            {
                Toggle();
            }
        }

        private void Build()
        {
            rows.Add(new HeaderRow("CHAIN   (the host decides these in a lobby)"));
            rows.Add(new BoolRow("Chained", settings.Enabled));
            rows.Add(new FloatRow("Chain length (m)", settings.ChainLength, 0.5f, 1f, 200f, "0.#"));
            rows.Add(new FloatRow("Slack before it pulls", settings.Slack, 0.01f, 0f, 0.5f));
            rows.Add(new FloatRow("Elasticity (0 = rigid)", settings.Elasticity, 0.05f, 0f, 0.98f));
            rows.Add(new FloatRow("Speed wins the tug", settings.SpeedPull, 0.05f, 0f, 1f));
            rows.Add(new FloatRow("Share of the pull", settings.Share, 0.05f, 0.05f, 1f));
            rows.Add(new FloatRow("Braking", settings.Damping, 0.05f, 0f, 1f));
            rows.Add(new FloatRow("Max pull per step (m)", settings.MaxCorrection, 0.05f, 0.02f, 5f));

            rows.Add(new HeaderRow("LAUNCH   (boost pads and cannons)"));
            rows.Add(new FloatRow("Chain slack on a launch (s)", settings.LaunchSuspend, 0.5f, 0f, 20f, "0.#"));
            rows.Add(new FloatRow("Slack after landing (s)", settings.LaunchGrace, 0.1f, 0f, 10f, "0.#"));
            rows.Add(new BoolRow("Share their pad launches", settings.ShareLaunches));
            rows.Add(new FloatRow("Launch range (m)", settings.LaunchRange, 1f, 2f, 500f, "0"));
            rows.Add(new FloatRow("Countdown (s)", settings.CountdownSeconds, 1f, 1f, 30f, "0"));

            rows.Add(new HeaderRow("RESCUE"));
            rows.Add(new BoolRow("Rescue keeps their speed", settings.RescueKeepsSpeed));
            rows.Add(new BoolRow("Rescue teleport", settings.RescueEnabled));
            rows.Add(new FloatRow("Rescue distance (chains)", settings.RescueDistance, 0.5f, 1.5f, 50f, "0.#")
               );
            rows.Add(new FloatRow("Rescue delay (s)", settings.RescueDelay, 0.1f, 0f, 10f));
            rows.Add(new FloatRow("Anchor time after respawn (s)", settings.RespawnGrace, 0.5f, 0f, 30f));

            rows.Add(new HeaderRow("LOOK & BINDS"));
            rows.Add(new BoolRow("Draw the chain", settings.ShowChain));
            rows.Add(new FloatRow("Chain thickness (m)", settings.ChainWidth, 0.01f, 0.005f, 1f));
            rows.Add(new FloatRow("Chain droop", settings.ChainSag, 0.05f, 0f, 2f));
            rows.Add(new BoolRow("Corner readout", settings.ShowStatus));
            rows.Add(new KeyRow("Countdown key", settings.CountdownKey));
            rows.Add(new KeyRow("Chain on/off key", settings.ToggleKey));
            rows.Add(new KeyRow("Menu key", settings.MenuKey));
        }

        private void Step(int direction)
        {
            CancelCapture();

            for (int i = 0; i < rows.Count; i++)
            {
                selected = (selected + direction + rows.Count) % rows.Count;

                if (rows[selected].Selectable)
                {
                    return;
                }
            }
        }

        private int FirstSelectable()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Selectable)
                {
                    return i;
                }
            }

            return 0;
        }

        private void CancelCapture()
        {
            if (rows[selected] is KeyRow key)
            {
                key.CancelCapture();
            }
        }

        /// <summary>Stops the arrow keys and the rest from reaching the game while the menu is up.</summary>
        private void SetGameInputBlocked(bool blocked)
        {
            if (!blocked && !blockingGameInput)
            {
                return;
            }

            try
            {
                PlayerRef player = GameRefs.LocalPlayer;

                if (player?.Movement != null)
                {
                    player.Movement.BlockInput = blocked;
                }

                if (player?.MovementJump != null)
                {
                    jumpToken ??= new Il2CppSystem.Object();

                    if (blocked)
                    {
                        player.MovementJump.AddJumpBlock(jumpToken);
                    }
                    else
                    {
                        player.MovementJump.RemoveJumpBlock(jumpToken);
                    }
                }

                blockingGameInput = blocked;
            }
            catch (Exception ex)
            {
                ChainedPlugin.Logger.LogError($"Could not {(blocked ? "block" : "restore")} game input: {ex}");
            }
        }

        private void EnsureStyles()
        {
            if (titleStyle != null)
            {
                return;
            }

            titleStyle = Gui.Style(16, FontStyle.Bold, Gui.Accent, TextAnchor.UpperLeft);
            labelStyle = Gui.Style(13, FontStyle.Normal, Gui.Normal, TextAnchor.MiddleLeft);
            valueStyle = Gui.Style(13, FontStyle.Bold, Gui.Highlight, TextAnchor.MiddleRight);
            headerStyle = Gui.Style(12, FontStyle.Bold, Gui.Dimmed, TextAnchor.LowerLeft);
            hintStyle = Gui.Style(11, FontStyle.Normal, Gui.Dimmed, TextAnchor.MiddleLeft);
            remoteStyle = Gui.Style(13, FontStyle.Normal, Gui.Dimmed, TextAnchor.MiddleRight);
        }
    }
}
