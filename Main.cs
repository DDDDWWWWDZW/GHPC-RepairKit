using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(RepairKit.RepairKitMod), "RepairKit", "1.0.0", "local")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace RepairKit
{
    /// <summary>
    /// 维修系统：按一次配置的按键进入维修状态，持续若干秒后把载具
    /// 受损部件的血量补到 70%（可配置），并恢复全部功能。
    /// 维修中不能移动、不能开火；再按一次、受到伤害、离车或车组失能都会中断。
    /// </summary>
    public class RepairKitMod : MelonMod
    {
        public const string CategoryId = "RepairKit";

        public static MelonPreferences_Category Cfg;

        // ------------------------------------------------------------------
        // 配置一律按键名现取，支持运行中热重载
        // ------------------------------------------------------------------

        public static string ReadString(string id, string fallback)
        {
            try
            {
                if (!MelonPreferences.HasEntry(CategoryId, id)) return fallback;

                string value = MelonPreferences.GetEntryValue<string>(CategoryId, id);

                return string.IsNullOrEmpty(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }

        public static float ReadFloat(string id, float fallback)
        {
            try
            {
                if (!MelonPreferences.HasEntry(CategoryId, id)) return fallback;

                return MelonPreferences.GetEntryValue<float>(CategoryId, id);
            }
            catch
            {
                return fallback;
            }
        }

        public static bool ReadBool(string id, bool fallback)
        {
            try
            {
                if (!MelonPreferences.HasEntry(CategoryId, id)) return fallback;

                return MelonPreferences.GetEntryValue<bool>(CategoryId, id);
            }
            catch
            {
                return fallback;
            }
        }

        // ------------------------------------------------------------------

        public override void OnInitializeMelon()
        {
            Cfg = MelonPreferences.CreateCategory(CategoryId, "Repair System");

            MelonPreferences_Entry<string> key = Cfg.CreateEntry<string>(
                "RepairKey", "J", "Repair Key");
            key.Description =
                "Press once to start repairing; press again while repairing to cancel "
                + "(no repair is applied). Use a Unity KeyCode name, e.g. J, K, F5, Keypad0.";
            key.Comment = "Default: J";

            MelonPreferences_Entry<float> duration = Cfg.CreateEntry<float>(
                "RepairDuration", 30f, "Repair Duration (seconds)");
            duration.Description =
                "How long the repair state must be maintained before it completes. "
                + "Taking damage during the repair aborts it immediately.";
            duration.Comment = "Default: 30";

            MelonPreferences_Entry<float> health = Cfg.CreateEntry<float>(
                "RepairHealthPercent", 70f, "Component Health After Repair (%)");
            health.Description =
                "Repair only restores damaged components up to this percentage, but all "
                + "functionality (engine, transmission, turret, aiming, weapons, ammo feed, "
                + "etc.) is restored. Components already above this value are left unchanged.";
            health.Comment = "Default: 70, range 1-100";

            MelonPreferences_Entry<bool> hud = Cfg.CreateEntry<bool>(
                "ShowHud", true, "Show Repair Progress");
            hud.Description = "Show the repair progress bar and result messages at the bottom of the screen.";
            hud.Comment = "true / false";

            RepairSession.Init();
        }

        public override void OnUpdate()
        {
            RepairSession.Update();
        }

        public override void OnGUI()
        {
            RepairSession.DrawHud();
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            // 必须在卸载时清，而不是加载后清：新场景里 Awake 已经跑完、
            // 基准值刚登记好，OnSceneWasLoaded 再清就全白记了。
            RepairApplier.ClearBaselines();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            RepairSession.OnSceneLoaded();
        }
    }
}
