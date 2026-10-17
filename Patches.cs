using GHPC;
using GHPC.Weapons;
using HarmonyLib;

namespace RepairKit
{
    // ----------------------------------------------------------------------
    // 开火封锁：维修状态下，本车所有开火入口直接返回。
    // ----------------------------------------------------------------------

    [HarmonyPatch(typeof(MainGun), "Fire")]
    internal static class MainGunFirePatch
    {
        private static bool Prefix(MainGun __instance)
        {
            return !RepairSession.ShouldBlockFire(__instance.GetComponentInParent<Unit>());
        }
    }

    [HarmonyPatch(typeof(MissileLauncher), "Fire")]
    internal static class MissileLauncherFirePatch
    {
        private static bool Prefix(MissileLauncher __instance)
        {
            return !RepairSession.ShouldBlockFire(__instance.GetComponentInParent<Unit>());
        }
    }

    [HarmonyPatch(typeof(WeaponSystem), "Fire")]
    internal static class WeaponSystemFirePatch
    {
        private static bool Prefix(WeaponSystem __instance, ref bool __result)
        {
            if (RepairSession.ShouldBlockFire(__instance.GetComponentInParent<Unit>()))
            {
                // 原方法返回 bool（是否成功开火）
                __result = false;
                return false;
            }

            return true;
        }
    }

    // ----------------------------------------------------------------------
    // 基准值采集
    //
    // SlewableTurret.KillPower 会把 TraverseRate / ElevationRate 改写成
    // NoPower 版本并丢失原值；NwhChassis.KillTransmission 会把
    // transmissionType 改成手动。必须在 Awake 之后马上记下原始值。
    // ----------------------------------------------------------------------

    [HarmonyPatch(typeof(SlewableTurret), "Awake")]
    internal static class TurretBaselinePatch
    {
        private static void Postfix(SlewableTurret __instance)
        {
            RepairApplier.RegisterTurret(__instance);
        }
    }

    [HarmonyPatch(typeof(NwhChassis), "Awake")]
    internal static class ChassisBaselinePatch
    {
        private static void Postfix(NwhChassis __instance)
        {
            RepairApplier.RegisterChassis(__instance);
        }
    }
}
