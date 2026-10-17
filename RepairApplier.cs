using System.Collections.Generic;
using GHPC;
using GHPC.Camera;
using GHPC.Equipment;
using GHPC.Vehicle;
using GHPC.Weapons;
using UnityEngine;

namespace RepairKit
{
    /// <summary>
    /// 真正执行维修：先把部件血量补到目标值，再逐系统清掉“已损坏”标记，
    /// 让发动机/传动/炮塔/火控/武器等恢复功能。
    ///
    /// 注意顺序：血量写入会触发 HealthChanged，进而把 ChassisDamageManager
    /// 的 _engineDamaged 等标记重新置真，所以必须先写血量、再清标记。
    ///
    /// 不处理：乘员、弹药（只恢复供弹机构）、外观（车体凹陷/贴图）。
    /// </summary>
    internal static class RepairApplier
    {
        private struct TurretBaseline
        {
            public SlewableTurret owner;
            public float traverse;
            public float elevation;
            public float drag;
        }

        private struct ChassisBaseline
        {
            public NwhChassis owner;
            public int transmissionType;
        }

        private static readonly Dictionary<int, TurretBaseline> _turretBase =
            new Dictionary<int, TurretBaseline>();

        private static readonly Dictionary<int, ChassisBaseline> _chassisBase =
            new Dictionary<int, ChassisBaseline>();

        /// <summary>在 SlewableTurret.Awake 之后记录原始转速，KillPower 会覆盖它们。</summary>
        public static void RegisterTurret(SlewableTurret turret)
        {
            if (turret == null) return;

            TurretBaseline b = new TurretBaseline();
            b.owner = turret;
            b.traverse = turret.TraverseRate;
            b.elevation = turret.ElevationRate;
            b.drag = 0f;

            Rigidbody rb = turret.GetComponent<Rigidbody>();

            if (rb != null) b.drag = rb.drag;

            _turretBase[turret.GetInstanceID()] = b;
        }

        /// <summary>在 NwhChassis.Awake 之后记录原始变速箱模式，KillTransmission 会改成手动。</summary>
        public static void RegisterChassis(NwhChassis chassis)
        {
            if (chassis == null) return;

            try
            {
                NWH.VehiclePhysics.VehicleController vc = chassis.VehicleController;

                if (vc == null || vc.transmission == null) return;

                ChassisBaseline b = new ChassisBaseline();
                b.owner = chassis;
                b.transmissionType = (int)vc.transmission.transmissionType;

                _chassisBase[chassis.GetInstanceID()] = b;
            }
            catch
            {
            }
        }

        public static void ClearBaselines()
        {
            _turretBase.Clear();
            _chassisBase.Clear();
        }

        // ------------------------------------------------------------------

        public static bool IsVehiclePart(GHPC.Equipment.DestructibleComponent comp, Unit unit)
        {
            if (comp == null) return false;

            // 乘员身体部件：维修明确不包括成员。
            // 健康/事件在 GHPC.Equipment.DestructibleComponent 上，而 IsCrew 标记
            // 在另一个同名（全局命名空间）的 DestructibleComponent 上，两者是兄弟类，
            // 通常挂在同一个 GameObject 上。
            global::DestructibleComponent marker = comp.GetComponent<global::DestructibleComponent>();

            if (marker != null && marker.IsCrew) return false;
            if (comp.GetComponentInParent<GHPC.Humans.Human>() != null) return false;

            // Unit 字段只用于排除“不属于本车”的部件；为空时按层级判断（我们已经只扫本车子树）
            if (comp.Unit != null && comp.Unit != unit) return false;

            return true;
        }

        // ------------------------------------------------------------------

        public static void Apply(Unit unit, NwhChassis chassis)
        {
            if (unit == null) return;

            float target = RepairKitMod.ReadFloat("RepairHealthPercent", 70f);
            target = Mathf.Clamp(target, 1f, 100f);

            RestorePartHealth(unit, target);
            RestoreChassisDamageFlags(unit);
            RestoreDrivableChassis(unit);
            RestoreChassisPhysicalState(chassis);
            RestoreTurret(unit);
            RestoreAimablePlatforms(unit);
            RestoreFireControl(unit);
            RestoreWeaponSystems(unit);
            RestoreAmmoFeeds(unit);
            RestoreGuns(unit);
            RestoreOptics(unit);
            RestoreEquipmentPower(unit);
            RestoreAiCrew(unit);
            RestoreVehicleAudio(unit);
            RestorePlayerControl();

            // 载具状态提示恢复
            unit.CannotMove = false;
            unit.CannotShoot = false;
        }

        // ------------------------------------------------------------------

        private static void RestorePartHealth(Unit unit, float target)
        {
            GHPC.Equipment.DestructibleComponent[] comps =
                unit.GetComponentsInChildren<GHPC.Equipment.DestructibleComponent>(true);

            for (int i = 0; i < comps.Length; i++)
            {
                GHPC.Equipment.DestructibleComponent comp = comps[i];

                if (!IsVehiclePart(comp, unit)) continue;

                // 已经比目标值好的部件不动，避免“修一次反而掉血”
                if (comp.HealthPercent >= target) continue;

                comp.SetHealthPercent(target);
            }
        }

        private static void RestoreChassisDamageFlags(Unit unit)
        {
            ChassisDamageManager dmg = unit.GetComponentInChildren<ChassisDamageManager>(true);

            if (dmg == null) return;

            dmg._engineDestroyed = false;
            dmg._engineDamaged = false;
            dmg._transmissionDestroyed = false;
            dmg._transmissionDamaged = false;
            dmg._radiatorDestroyed = false;
            dmg._leftTrackDestroyed = false;
            dmg._rightTrackDestroyed = false;

            dmg.updateDamageText();
        }

        /// <summary>
        /// DrivableChassis 才是伤害效果（炮塔断电、发动机熄火、断履带、传动受损）
        /// 的持久状态持有者。NwhChassis/DrivableChassis 的标记都要清，否则
        /// SetEngineOn(true) 会直接 return，车永远动不了。
        /// </summary>
        private static void RestoreDrivableChassis(Unit unit)
        {
            DrivableChassis dc = unit.GetComponentInChildren<DrivableChassis>(true);

            if (dc == null) return;

            dc._killedTracks = false;
            dc._disabledEngine = false;
            dc._killedEngine = false;
            dc._damagedDrivetrain = false;
            dc._killedFuel = false;
            dc._ignitedFuel = false;
            dc._compartmentFire = false;
            dc._reportedFire = false;
            dc._statusMessage = "";

            // 把还在烧的易燃物熄掉（弹药是否复原则不在维修范围内）
            GHPC.Effects.FlammablesManager flammables =
                unit.GetComponentInChildren<GHPC.Effects.FlammablesManager>(true);

            if (flammables != null && flammables.Compartments != null)
            {
                for (int i = 0; i < flammables.Compartments.Length; i++)
                {
                    GHPC.Effects.Compartment compartment = flammables.Compartments[i];

                    if (compartment == null) continue;

                    if (compartment.Clusters != null)
                    {
                        foreach (GHPC.Effects.FlammablesCluster cluster in compartment.Clusters)
                        {
                            if (cluster != null) cluster.Extinguish(true);
                        }
                    }

                    compartment.FirePresent = false;
                    compartment.CombinedFlameHeight = 0f;
                }
            }

            // 标记清完再打开，否则会被上面的 return 挡掉
            dc.SetEngineOn(true);

            // SetEngineOn(true) 在 _killedAmmo 为真时会提前 return（弹药架被毁的连带限制），
            // 那里不会刷新音源音量；这里兜底把旧版 AudioSource 的引擎声也拉回来。
            if (dc._sfx != null) dc._sfx.EngineOn = true;
        }

        private static void RestoreChassisPhysicalState(NwhChassis chassis)
        {
            if (chassis == null) return;

            chassis._broken = false;
            chassis._parkingBrake = false;
            chassis._disabled = false;
            chassis._intendedSpeed = 0f;

            try
            {
                NWH.VehiclePhysics.VehicleController vc = chassis.VehicleController;

                if (vc == null) return;

                // 这两个方法会用 Awake 时保存的原始值重新计算
                chassis.SetEnginePowerPercent(100f);
                chassis.SetTransmissionIntegrityPercent(100f);

                ChassisBaseline baseline;

                // owner 为空说明旧场景的物体已销毁、实例 ID 被复用，不能拿来用
                if (vc.transmission != null
                    && _chassisBase.TryGetValue(chassis.GetInstanceID(), out baseline)
                    && baseline.owner != null)
                {
                    // TransmissionType 是 Transmission 内部的嵌套枚举（Manual=0, Automatic=1, ...）
                    vc.transmission.transmissionType =
                        (NWH.VehiclePhysics.Transmission.TransmissionType)baseline.transmissionType;
                }

                if (vc.engine != null && !vc.engine.IsRunning)
                {
                    // 发动机被 KillEngine 停掉后需要重新启动；本来就在转的不要重放启动流程
                    vc.engine.Start();
                }

                if (vc.drivingAssists != null && vc.drivingAssists.abs != null)
                {
                    vc.drivingAssists.abs.enabled = true;
                }
            }
            catch
            {
            }
        }

        private static void RestoreTurret(Unit unit)
        {
            SlewableTurret[] turrets = unit.GetComponentsInChildren<SlewableTurret>(true);

            for (int i = 0; i < turrets.Length; i++)
            {
                SlewableTurret t = turrets[i];

                if (t == null) continue;

                t._killedPower = false;
                t._killedTraverse = false;
                t._canTraverse = true;
                t._damagedGun = false;
                t._damagedMissile = false;
                t._damagedCommanderViewer = false;
                t._disabledDigitalSight = false;
                t._acceptNewInput = true;
                t._allowPid = true;
                t._statusMessage = "";

                // KillPower 把转速换成了 NoPower 值，这里换回 Awake 时的原始值。
                // 弹药架/炮塔炸飞属于不可逆后果，不动 _killedAmmo / PoppedTurret。
                TurretBaseline baseline;

                if (_turretBase.TryGetValue(t.GetInstanceID(), out baseline) && baseline.owner != null)
                {
                    t.TraverseRate = baseline.traverse;
                    t.ElevationRate = baseline.elevation;

                    // Disable() 会把炮塔刚体阻力改成 0.8
                    Rigidbody rb = t.GetComponent<Rigidbody>();

                    if (rb != null) rb.drag = baseline.drag;
                }
            }
        }

        /// <summary>
        /// AI 炮手/车长也各自缓存了一份“炮损坏/数字瞄具损坏/车长镜损坏”标记，
        /// 不清理的话玩家换座位或交给 AI 时仍然不会开火。
        /// </summary>
        private static void RestoreAiCrew(Unit unit)
        {
            AiGunner[] gunners = unit.GetComponentsInChildren<AiGunner>(true);

            for (int i = 0; i < gunners.Length; i++)
            {
                if (gunners[i] == null) continue;

                gunners[i]._digitalSightDisabled = false;
                gunners[i]._gunDamaged = false;
            }

            AiCommander[] commanders = unit.GetComponentsInChildren<AiCommander>(true);

            for (int i = 0; i < commanders.Length; i++)
            {
                if (commanders[i] == null) continue;

                commanders[i]._digitalSightDisabled = false;
                commanders[i]._commanderViewerDisabled = false;
            }
        }

        /// <summary>
        /// 修复引擎声音。
        ///
        /// 引擎被击毁时 NwhChassis.KillDriving 会触发 EngineStopped 事件，
        /// VehicleAudioController 在 EnableAudio 里把 StopEngine 订阅到了这个事件，
        /// 于是 FMOD 的引擎事件实例被 Destroy 掉并置空。全工程只有组件 OnEnable
        /// 会调用 StartEngine，所以修好引擎后声音不会自己回来。
        ///
        /// 这里补建实例：已有实例就不要重复创建，否则会出现两份引擎声。
        /// </summary>
        private static void RestoreVehicleAudio(Unit unit)
        {
            GHPC.Audio.VehicleAudioController[] controllers =
                unit.GetComponentsInChildren<GHPC.Audio.VehicleAudioController>(true);

            for (int i = 0; i < controllers.Length; i++)
            {
                GHPC.Audio.VehicleAudioController audio = controllers[i];

                if (audio == null || !audio.enabled || !audio.IsInitialized) continue;
                if (audio._engineInstance != null) continue;

                MelonLoader.MelonLogger.Msg(
                    "[RepairKit] Rebuilt engine audio instance: " + audio.name);

                audio.StartEngine();
            }
        }

        private static void RestoreAimablePlatforms(Unit unit)
        {
            AimablePlatform[] platforms = unit.GetComponentsInChildren<AimablePlatform>(true);

            for (int i = 0; i < platforms.Length; i++)
            {
                AimablePlatform p = platforms[i];

                if (p == null) continue;

                // 等价于 hitZoneRepaired()，但那个方法要求传入触发事件的部件
                p._destroyed = false;
                p.DamageStatus = "";
                p.SetPowerActive(true);   // PowerEnabled = true
                p.EnableAiming();
            }
        }

        private static void RestoreFireControl(Unit unit)
        {
            FireControlSystem[] systems = unit.GetComponentsInChildren<FireControlSystem>(true);

            for (int i = 0; i < systems.Length; i++)
            {
                FireControlSystem f = systems[i];

                if (f == null) continue;

                f._laserDestroyed = false;
                f._computerDestroyed = false;
                f._powerLost = false;
            }
        }

        private static void RestoreWeaponSystems(Unit unit)
        {
            WeaponSystem[] weapons = unit.GetComponentsInChildren<WeaponSystem>(true);

            for (int i = 0; i < weapons.Length; i++)
            {
                WeaponSystem w = weapons[i];

                if (w == null) continue;

                w._barrelDamaged = false;
                w._breechDamaged = false;
                w._guidanceDamaged = false;
                w.recalculateShotDeviation();
            }
        }

        private static void RestoreAmmoFeeds(Unit unit)
        {
            AmmoFeed[] feeds = unit.GetComponentsInChildren<AmmoFeed>(true);

            for (int i = 0; i < feeds.Length; i++)
            {
                AmmoFeed f = feeds[i];

                if (f == null) continue;

                f._autoloaderDestroyed = false;

                // AutoloaderDestroyed 会把装填时间乘上惩罚倍率，这里按未损坏重算
                f.CalculateTotalTime(f.RoundCycleStages, false);
                f.CalculateTotalTime(f.ClipReloadStages, false);
            }
        }

        private static void RestoreGuns(Unit unit)
        {
            MainGun[] guns = unit.GetComponentsInChildren<MainGun>(true);

            for (int i = 0; i < guns.Length; i++)
            {
                MainGun g = guns[i];

                if (g == null) continue;

                // _loaderDamaged 由乘员受伤触发，属于“成员”范畴，不在这里恢复
                g._gunDamaged = false;
                g.recalculateShotDeviation();
            }

            MissileLauncher[] launchers = unit.GetComponentsInChildren<MissileLauncher>(true);

            for (int i = 0; i < launchers.Length; i++)
            {
                MissileLauncher m = launchers[i];

                if (m == null) continue;

                m._tubeDamaged = false;
            }
        }

        private static void RestoreOptics(Unit unit)
        {
            CameraSlot[] slots = unit.GetComponentsInChildren<CameraSlot>(true);

            for (int i = 0; i < slots.Length; i++)
            {
                CameraSlot s = slots[i];

                if (s == null) continue;

                s._isUsableByDamage = true;
                s._damageStatus = "";
                s.Damaged = false;
                s.RefreshAvailability();
            }
        }

        private static void RestoreEquipmentPower(Unit unit)
        {
            EquipmentManager equipment = unit.GetComponentInChildren<EquipmentManager>(true);

            if (equipment == null) return;

            equipment._powerOn = true;
            equipment.Damaged = false;

            // KillPower 把所有灯具的 Active 置假。恢复“可用”，但保持关闭状态，
            // 免得维修一完成全车灯自己亮起来。
            if (equipment.AllLamps == null) return;

            for (int i = 0; i < equipment.AllLamps.Length; i++)
            {
                if (equipment.AllLamps[i] == null) continue;

                equipment.AllLamps[i].Active = true;
            }
        }

#pragma warning disable 0612
        private static void RestorePlayerControl()
        {
            PlayerControl pc = PlayerControl.Instance;

            if (pc == null) return;

            pc._turretPowerKilled = false;
            pc._digitalSightDestroyed = false;
            pc._hardKilled = false;
            pc._gunDamaged = false;
        }
#pragma warning restore 0612
    }
}
