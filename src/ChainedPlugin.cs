using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChainedIsHard
{
    [BepInPlugin(Guid, "Chained Is Hard", "1.0.0")]
    public class ChainedPlugin : BasePlugin
    {
        public const string Guid = "com.dani.chainedishard";

        internal static ChainedPlugin Instance { get; private set; }
        internal static ManualLogSource Logger { get; private set; }
        internal static ChainedSettings Settings { get; private set; }

        /// <summary>Where the config lives: the mod's own folder.</summary>
        internal static string DataFolder { get; private set; }

        public override void Load()
        {
            Instance = this;
            Logger = Log;

            // The config file lives next to the dll instead of BepInEx\config, so the whole
            // mod is one folder you can copy between installs and keep your binds.
            DataFolder = Path.GetDirectoryName(typeof(ChainedPlugin).Assembly.Location) ?? Paths.ConfigPath;
            string path = Path.Combine(DataFolder, "ChainedIsHard.cfg");

            Settings = new ChainedSettings(new ConfigFile(path, true));
            Settings.Validate();

            // Has to come before anything can read a setting: it builds the list the host
            // publishes and the clients follow.
            ChainNetwork.Start(Settings);

            ClassInjector.RegisterTypeInIl2Cpp<ChainedBehaviour>();

            var host = new GameObject("ChainedIsHard");
            Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<ChainedBehaviour>();

            Logger.LogInfo($"Loaded. {Settings.Summary()}. " +
                           $"Menu={Settings.MenuKey.Value} Toggle={Settings.ToggleKey.Value}");
            Logger.LogInfo($"Settings file: {path}");
        }

        public override bool Unload()
        {
            Settings?.Save();
            return true;
        }
    }
}
