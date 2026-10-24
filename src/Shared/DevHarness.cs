#if DEV
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Managers.Interface;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Stellar.Shared
{
    /// <summary>
    /// Dev-only remote control, linked into every mod of the suite. Each mod polls its own
    /// UserData/stellar_&lt;mod&gt;_cmd.txt and runs each line as a command; one of them (StellarBuild)
    /// also loads the test world straight from the main menu.
    /// </summary>
    internal sealed class DevHarness
    {
        public const string TestWorld = "SF_Test";

        private readonly MelonLogger.Instance _log;
        private readonly string _cmdFile;
        private readonly bool _autoLaunch;
        private readonly Dictionary<string, Action<string[]>> _commands = new Dictionary<string, Action<string[]>>();
        private float _nextPoll;
        private bool _launched;

        /// <summary>Dev multiplayer on one PC (STELLAR_NET=host|client): the host serves over IP (Tugboat), the client joins localhost.</summary>
        public static readonly string Role = Environment.GetEnvironmentVariable("STELLAR_NET") ?? "";

        public DevHarness(string modId, MelonLogger.Instance log, bool autoLaunch, HarmonyLib.Harmony harmony = null)
        {
            _log = log;
            _autoLaunch = autoLaunch;
            _cmdFile = Path.Combine(MelonEnvironment.UserDataDirectory, "stellar_" + modId + (Role.Length > 0 ? "_" + Role : "") + "_cmd.txt");
            if (autoLaunch && Role.Length > 0 && harmony != null)
                harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Managers.Common.MainGameSceneInitializer), "Start"),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(DevHarness), nameof(NetStartPrefix)));
            if (File.Exists(_cmdFile)) File.Delete(_cmdFile);
            Command("shot", a =>
            {
                string path = Path.Combine(MelonEnvironment.UserDataDirectory, "shot_" + Role + (a.Length > 1 ? a[1] : "x") + ".png");
                ScreenCapture.CaptureScreenshot(path);
                _log.Msg($"[DEV] screenshot {path}");
            });
            Command("quit", a => Application.Quit());
        }

        private static bool NetStartPrefix(Managers.Common.MainGameSceneInitializer __instance)
        {
            var t = HarmonyLib.Traverse.Create(__instance);
            if (Role == "host")
            {
                MainGameSceneLoadData.Mode = MainGameSceneLoadData.GameMode.HostGameIp;
                MainGameSceneLoadData.FakeLocalSteamUserId = 1001;
                t.Method("StartHostIp").GetValue();
            }
            else
            {
                MainGameSceneLoadData.Mode = MainGameSceneLoadData.GameMode.JoinGameIp;
                MainGameSceneLoadData.FakeLocalSteamUserId = 1002;
                t.Method("StartClientIp").GetValue();
            }
            return false;
        }

        public void Command(string name, Action<string[]> run) => _commands[name] = run;

        /// <summary>Runs one command line now.</summary>
        public void Run(string line)
        {
            string[] a = line.Split(' ');
            if (_commands.TryGetValue(a[0], out Action<string[]> run)) run(a);
        }

        public void SceneLoaded(int buildIndex, string sceneName)
        {
            if (!_autoLaunch) return;
            _log.Msg($"[DEV] scene loaded {buildIndex} {sceneName}");
            if (buildIndex == 0 && !_launched && Environment.GetEnvironmentVariable("STELLAR_AUTOLAUNCH") != "0")
            {
                _launched = true;
                MelonCoroutines.Start(LaunchTestWorld());
            }
        }

        private IEnumerator LaunchTestWorld()
        {
            for (int i = 0; i < 30; i++) yield return null;
            string file = Path.Combine(Application.persistentDataPath, "WorldSaves", TestWorld, "world.json");
            if (!File.Exists(file))
            {
                _log.Error($"[DEV] test world missing: {file}");
                yield break;
            }
            MainGameSceneLoadData.Mode = MainGameSceneLoadData.GameMode.SinglePlayer;
            MainGameSceneLoadData.SaveFileFullName = new FileInfo(file).FullName;
            _log.Msg($"[DEV] launching {TestWorld}");
            SceneManager.LoadScene("MainGame", LoadSceneMode.Single);
        }

        public void Update()
        {
            if (Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + 0.5f;
            if (!File.Exists(_cmdFile)) return;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(_cmdFile);
                File.Delete(_cmdFile);
            }
            catch (IOException)
            {
                return;
            }
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                _log.Msg($"[DEV] cmd: {line}");
                string[] a = line.Split(' ');
                try
                {
                    if (_commands.TryGetValue(a[0], out Action<string[]> run)) run(a);
                    else _log.Warning($"[DEV] unknown command {a[0]}");
                }
                catch (Exception ex)
                {
                    _log.Error($"[DEV] cmd failed: {ex}");
                }
            }
        }
    }
}
#endif
