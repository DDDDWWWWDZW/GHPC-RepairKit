using System;
using System.Collections.Generic;
using GHPC;
using GHPC.Player;
using MelonLoader;
using UnityEngine;

namespace RepairKit
{
    /// <summary>
    /// 维修状态机：Idle -> Repairing -> (完成 / 中断) -> Idle。
    /// 负责按键、锁移动、伤害检测和进度显示；真正改数据在 RepairApplier。
    /// </summary>
    internal static class RepairSession
    {
        private enum State
        {
            Idle,
            Repairing
        }

        private struct Watched
        {
            public GHPC.Equipment.DestructibleComponent comp;
            public float health;
        }

        private static State _state;

        private static Unit _unit;
        private static NwhChassis _chassis;

        private static float _duration;
        private static float _endTime;

        // 进入维修前保存的底盘状态，取消时原样还回去
        private static bool _savedBroken;
        private static bool _savedParking;
        private static bool _savedDisabled;
        private static bool _savedCruise;
        private static float _savedIntendedSpeed;

        private static readonly List<Watched> _watched = new List<Watched>();

        private static string _toast;
        private static float _toastUntil;

        private static KeyCode _key = KeyCode.J;
        private static string _keyRaw = "J";

        private static GUIStyle _labelStyle;
        private static Texture2D _white;

        public static void Init()
        {
            _state = State.Idle;
            _unit = null;
            _chassis = null;
            _watched.Clear();
            _toast = null;
            _toastUntil = 0f;
        }

        public static bool ShouldBlockFire(Unit unit)
        {
            return _state == State.Repairing && unit != null && unit == _unit;
        }

        public static void OnSceneLoaded()
        {
            if (_state == State.Repairing)
            {
                // 场景都换了，没必要再恢复旧车的状态
                _state = State.Idle;
                _watched.Clear();
                _unit = null;
                _chassis = null;
            }
        }

        // ------------------------------------------------------------------

        public static void Update()
        {
            bool pressed = KeyPressedThisFrame();

            if (_state == State.Repairing)
            {
                PlayerInput input = PlayerInput.Instance;

                if (_unit == null || input == null || input.CurrentPlayerUnit != _unit)
                {
                    Cancel("Left the vehicle - repair aborted");
                    return;
                }

                if (_unit.Destroyed || _unit.Abandoned || !HasLivingCrew(_unit))
                {
                    Cancel("Vehicle destroyed, abandoned, or crew incapacitated - repair aborted");
                    return;
                }

                if (TookDamageSinceStart())
                {
                    Cancel("Vehicle took damage during repair - repair aborted");
                    return;
                }

                if (pressed)
                {
                    Cancel("Repair cancelled");
                    return;
                }

                HoldStill();

                if (Time.time >= _endTime)
                {
                    Complete();
                }

                return;
            }

            if (pressed)
            {
                TryStart();
            }
        }

        // ------------------------------------------------------------------

        private static void TryStart()
        {
            PlayerInput input = PlayerInput.Instance;

            if (input == null) return;

            Unit unit = input.CurrentPlayerUnit;

            if (unit == null)
            {
                Toast("Not in a vehicle");
                return;
            }

            if (unit.Destroyed || unit.Abandoned)
            {
                Toast("Vehicle is destroyed or abandoned - cannot repair");
                return;
            }

            if (!HasLivingCrew(unit))
            {
                Toast("All crew incapacitated - cannot repair");
                return;
            }

            NwhChassis chassis = unit.GetComponentInChildren<NwhChassis>(true);

            if (chassis == null)
            {
                Toast("This vehicle does not support repair");
                return;
            }

            // 行驶中不能进入维修。_broken 只能挡住后续的驾驶输入，
            // 挡不住已经在滚的惯性，所以必须在开始时就把车停住。
            if (IsMoving(chassis))
            {
                Toast("Stop the vehicle before repairing");
                return;
            }

            _unit = unit;
            _chassis = chassis;

            _savedBroken = chassis._broken;
            _savedParking = chassis._parkingBrake;
            _savedDisabled = chassis._disabled;
            _savedIntendedSpeed = chassis._intendedSpeed;
            _savedCruise = false;

            try
            {
                NWH.VehiclePhysics.VehicleController vc = chassis.VehicleController;

                if (vc != null && vc.drivingAssists != null && vc.drivingAssists.cruiseControl != null)
                {
                    _savedCruise = vc.drivingAssists.cruiseControl.enabled;
                }
            }
            catch
            {
                // NWH 结构异常时忽略，不影响维修本身
            }

            _duration = RepairKitMod.ReadFloat("RepairDuration", 30f);
            if (_duration < 1f) _duration = 1f;

            _endTime = Time.time + _duration;

            CollectWatchedParts(unit);
            HoldStill();

            _state = State.Repairing;
            Toast("Repairing...");
        }

        private static void Complete()
        {
            Unit unit = _unit;
            NwhChassis chassis = _chassis;

            _state = State.Idle;
            _watched.Clear();
            _unit = null;
            _chassis = null;

            try
            {
                RepairApplier.Apply(unit, chassis);
                Toast("Repair complete");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[RepairKit] Error while applying repair: " + e);
                Toast("Repair complete, but some systems failed to restore (see log)");
            }
        }

        private static void Cancel(string reason)
        {
            _state = State.Idle;
            _watched.Clear();

            if (_chassis != null)
            {
                _chassis._broken = _savedBroken;
                _chassis._parkingBrake = _savedParking;
                _chassis._disabled = _savedDisabled;
                _chassis._intendedSpeed = _savedIntendedSpeed;

                try
                {
                    NWH.VehiclePhysics.VehicleController vc = _chassis.VehicleController;

                    if (vc != null && vc.drivingAssists != null && vc.drivingAssists.cruiseControl != null)
                    {
                        vc.drivingAssists.cruiseControl.enabled = _savedCruise;
                    }
                }
                catch
                {
                }
            }

            _unit = null;
            _chassis = null;

            if (!string.IsNullOrEmpty(reason)) Toast(reason);
        }

        // ------------------------------------------------------------------
        // 移动锁
        // ------------------------------------------------------------------

        private static void HoldStill()
        {
            if (_chassis == null) return;

            // NwhChassis.SetSpeed / SetHeading / SetTurnAmount 都会先检查 _broken，
            // 把它置真就等于切断驾驶输入；_parkingBrake 负责把现有速度刹掉。
            _chassis._broken = true;
            _chassis._parkingBrake = true;
            _chassis._intendedSpeed = 0f;

            try
            {
                NWH.VehiclePhysics.VehicleController vc = _chassis.VehicleController;

                if (vc != null && vc.drivingAssists != null && vc.drivingAssists.cruiseControl != null)
                {
                    vc.drivingAssists.cruiseControl.enabled = false;
                }
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // 伤害检测 / 车组判定
        // ------------------------------------------------------------------

        private static void CollectWatchedParts(Unit unit)
        {
            _watched.Clear();

            GHPC.Equipment.DestructibleComponent[] comps =
                unit.GetComponentsInChildren<GHPC.Equipment.DestructibleComponent>(true);

            for (int i = 0; i < comps.Length; i++)
            {
                GHPC.Equipment.DestructibleComponent comp = comps[i];

                // 乘员/外观不在维修范围内，也不应触发“维修中受到伤害”
                if (!RepairApplier.IsVehiclePart(comp, unit)) continue;

                Watched w = new Watched();
                w.comp = comp;
                w.health = comp.HealthPercent;

                _watched.Add(w);
            }
        }

        private static bool TookDamageSinceStart()
        {
            for (int i = 0; i < _watched.Count; i++)
            {
                Watched w = _watched[i];

                if (w.comp == null) continue;

                if (w.comp.HealthPercent < w.health - 0.005f) return true;
            }

            return false;
        }

        /// <summary>
        /// 判定车辆是否仍在移动。用车体刚体的水平速度而不是驾驶输入，
        /// 这样松开油门后的滑行也会被算作“行驶中”。
        /// </summary>
        private static bool IsMoving(NwhChassis chassis)
        {
            const float threshold = 0.5f;   // m/s，约 1.8 km/h

            try
            {
                Rigidbody rb = chassis.Rigidbody;

                if (rb != null)
                {
                    Vector3 velocity = rb.velocity;
                    velocity.y = 0f;

                    return velocity.magnitude > threshold;
                }

                return Mathf.Abs(chassis.GetScalarSpeed()) > threshold;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasLivingCrew(Unit unit)
        {
            try
            {
                GHPC.Crew.CrewManager crew = unit.CrewManager;

                if (crew == null || crew.CrewMembers == null || crew.CrewMembers.Length == 0)
                {
                    // 没有车组概念（或无乘员数据）时不作为阻止条件
                    return true;
                }

                int known = 0;
                int alive = 0;

                for (int i = 0; i < crew.CrewMembers.Length; i++)
                {
                    var member = crew.CrewMembers[i];

                    if (member == null || member.Human == null) continue;

                    known++;

                    if (!member.Human.IsDead) alive++;
                }

                // 一个有效成员都没有时无法判断，放行
                if (known == 0) return true;

                return alive > 0;
            }
            catch
            {
                return true;
            }
        }

        // ------------------------------------------------------------------
        // 按键
        // ------------------------------------------------------------------

        private static bool KeyPressedThisFrame()
        {
            KeyCode key = ResolveKey();

            try
            {
                // 直接问 Rewired 的键盘，不依赖 Action 映射，所以任意 KeyCode 都能用。
                // Player.GetKeyDown(KeyCode) 在这版 Rewired 里不存在，用 ControllerHelper。
                Rewired.Keyboard keyboard = Rewired.ReInput.controllers.Keyboard;

                if (keyboard != null && keyboard.GetKeyDown(key)) return true;
            }
            catch
            {
                // Rewired 还没初始化完成
            }

            return false;
        }

        private static KeyCode ResolveKey()
        {
            string raw = RepairKitMod.ReadString("RepairKey", "J").Trim();

            if (raw == _keyRaw) return _key;

            KeyCode parsed;

            if (Enum.TryParse<KeyCode>(raw, true, out parsed))
            {
                _key = parsed;
                _keyRaw = raw;
            }
            else
            {
                _key = KeyCode.J;
                _keyRaw = "J";
            }

            return _key;
        }

        // ------------------------------------------------------------------
        // HUD
        // ------------------------------------------------------------------

        private static void Toast(string text)
        {
            _toast = text;
            _toastUntil = Time.time + 3f;
        }

        public static void DrawHud()
        {
            if (!RepairKitMod.ReadBool("ShowHud", true)) return;

            if (_state == State.Repairing)
            {
                float remain = Mathf.Max(0f, _endTime - Time.time);
                float progress = _duration > 0f ? Mathf.Clamp01(1f - remain / _duration) : 1f;

                DrawBar("Repairing  " + remain.ToString("0.0") + "s", progress);
                return;
            }

            if (!string.IsNullOrEmpty(_toast) && Time.time < _toastUntil)
            {
                EnsureStyle();

                float w = 420f;
                float x = (Screen.width - w) * 0.5f;
                float y = Screen.height - 150f;

                GUI.Label(new Rect(x, y, w, 26f), _toast, _labelStyle);
            }
        }

        private static void DrawBar(string text, float progress)
        {
            EnsureStyle();

            if (_white == null)
            {
                _white = new Texture2D(1, 1);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }

            float w = 380f;
            float h = 18f;
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height - 150f;

            GUI.Label(new Rect(x, y - 26f, w, 24f), text, _labelStyle);

            Color old = GUI.color;

            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(x, y, w, h), _white);

            GUI.color = new Color(0.25f, 0.85f, 0.35f, 0.95f);
            GUI.DrawTexture(new Rect(x, y, w * Mathf.Clamp01(progress), h), _white);

            GUI.color = old;
        }

        private static void EnsureStyle()
        {
            if (_labelStyle != null) return;

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 18;
            _labelStyle.alignment = TextAnchor.MiddleCenter;
            _labelStyle.normal.textColor = Color.white;
        }
    }
}
