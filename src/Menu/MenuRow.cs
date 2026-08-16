using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChainedIsHard.Menu
{
    /// <summary>One line of the menu. A header is a row that cannot be selected.</summary>
    internal abstract class MenuRow
    {
        protected MenuRow(string label)
        {
            Label = label;
        }

        public string Label { get; }

        public virtual bool Selectable => true;

        /// <summary>The setting this row edits, so the menu can tell which ones the host decides.</summary>
        public virtual ConfigEntryBase Entry => null;

        public abstract string Value { get; }

        /// <summary>Left/right on the row. Direction is -1 or 1.</summary>
        public abstract void Adjust(int direction);

        public abstract void Reset();
    }

    internal sealed class HeaderRow : MenuRow
    {
        public HeaderRow(string label) : base(label) { }

        public override bool Selectable => false;

        public override string Value => string.Empty;

        public override void Adjust(int direction) { }

        public override void Reset() { }
    }

    internal sealed class FloatRow : MenuRow
    {
        private readonly ConfigEntry<float> entry;
        private readonly float step;
        private readonly float min;
        private readonly float max;
        private readonly string format;

        public FloatRow(string label, ConfigEntry<float> entry, float step, float min, float max,
            string format = "0.###")
            : base(label)
        {
            this.entry = entry;
            this.step = step;
            this.min = min;
            this.max = max;
            this.format = format;
        }

        public override ConfigEntryBase Entry => entry;

        public override string Value => entry.Value.ToString(format);

        public override void Adjust(int direction)
        {
            // Rounded to the step so a value dragged with the arrow keys does not end up as
            // 0.11999999 in the config file.
            float raw = entry.Value + step * direction;
            float snapped = Mathf.Round(raw / step) * step;
            entry.Value = Mathf.Clamp(snapped, min, max);
        }

        public override void Reset() => entry.Value = (float)entry.DefaultValue;
    }

    internal sealed class BoolRow : MenuRow
    {
        private readonly ConfigEntry<bool> entry;

        public BoolRow(string label, ConfigEntry<bool> entry) : base(label)
        {
            this.entry = entry;
        }

        public override ConfigEntryBase Entry => entry;

        public override string Value => entry.Value ? "on" : "off";

        public override void Adjust(int direction) => entry.Value = !entry.Value;

        public override void Reset() => entry.Value = (bool)entry.DefaultValue;
    }

    internal sealed class KeyRow : MenuRow
    {
        private readonly ConfigEntry<Key> entry;

        public KeyRow(string label, ConfigEntry<Key> entry) : base(label)
        {
            this.entry = entry;
        }

        public bool Capturing { get; private set; }

        public override string Value => Capturing ? "press a key…" : entry.Value.ToString();

        public override void Adjust(int direction) => Capturing = true;

        public override void Reset() => entry.Value = (Key)entry.DefaultValue;

        /// <summary>Reads the next key pressed. Escape cancels.</summary>
        public void Capture()
        {
            Key pressed = InputReader.ReadAnyKey();

            if (pressed == Key.None)
            {
                return;
            }

            Capturing = false;

            if (pressed == Key.Escape)
            {
                return;
            }

            entry.Value = pressed;
            ChainedPlugin.Logger.LogInfo($"{Label} bound to {pressed}.");
        }

        public void CancelCapture() => Capturing = false;
    }
}
