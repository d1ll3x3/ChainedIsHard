using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace ChainedIsHard
{
    /// <summary>
    /// Single-key polling through the Input System, which is what the game uses.
    ///
    /// Note that the Input System reads physical key positions on a US layout, so a bind
    /// is a position on the keyboard and not the letter printed on the cap.
    /// </summary>
    internal static class InputReader
    {
        // BepInEx's interop does not expose the Keyboard[Key] indexer, so the control is
        // resolved through the property Unity generates for each key, and then cached.
        private static readonly Dictionary<Key, PropertyInfo> Properties = new();

        private const float RepeatDelay = 0.4f;
        private const float RepeatInterval = 0.06f;

        private static Key[] allKeys;

        public static bool WasPressedThisFrame(Key key)
        {
            if (key == Key.None)
            {
                return false;
            }

            KeyControl control = ResolveControl(key);
            return control != null && control.wasPressedThisFrame;
        }

        public static bool IsHeld(Key key)
        {
            if (key == Key.None)
            {
                return false;
            }

            KeyControl control = ResolveControl(key);
            return control != null && control.isPressed;
        }

        public static bool ShiftHeld => IsHeld(Key.LeftShift) || IsHeld(Key.RightShift);

        /// <summary>
        /// True when the key repeats: fires once immediately, then steadily while held.
        /// Used by the menu so a value can be dragged instead of tapped.
        /// </summary>
        public static bool WasPressedOrRepeating(Key key, ref float nextRepeatAt)
        {
            if (WasPressedThisFrame(key))
            {
                nextRepeatAt = Time.unscaledTime + RepeatDelay;
                return true;
            }

            if (!IsHeld(key))
            {
                nextRepeatAt = 0f;
                return false;
            }

            if (Time.unscaledTime < nextRepeatAt)
            {
                return false;
            }

            nextRepeatAt = Time.unscaledTime + RepeatInterval;
            return true;
        }

        /// <summary>First key pressed this frame, for rebinding. Key.None when there is none.</summary>
        public static Key ReadAnyKey()
        {
            if (Keyboard.current == null)
            {
                return Key.None;
            }

            allKeys ??= (Key[])Enum.GetValues(typeof(Key));

            foreach (Key key in allKeys)
            {
                if (key != Key.None && WasPressedThisFrame(key))
                {
                    return key;
                }
            }

            return Key.None;
        }

        private static KeyControl ResolveControl(Key key)
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return null;
            }

            if (!Properties.TryGetValue(key, out PropertyInfo property))
            {
                property = typeof(Keyboard).GetProperty(ToPropertyName(key),
                    BindingFlags.Public | BindingFlags.Instance);

                if (property == null)
                {
                    ChainedPlugin.Logger.LogWarning($"Key '{key}' has no control on Keyboard, ignoring it.");
                }

                Properties[key] = property;
            }

            return property?.GetValue(keyboard) as KeyControl;
        }

        // Key.LeftBracket -> leftBracketKey, Key.Space -> spaceKey, Key.A -> aKey
        private static string ToPropertyName(Key key)
        {
            string name = key.ToString();
            return char.ToLowerInvariant(name[0]) + name.Substring(1) + "Key";
        }
    }
}
