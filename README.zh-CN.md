# RepairKit —— GHPC 载具维修模组

按一次按键，载具进入维修状态；持续若干秒后，受损部件血量补到设定值（默认 70%），
同时发动机、传动、炮塔、火控、武器、瞄具、供弹等**全部功能恢复正常**。

维修不涉及外观凹陷、乘员伤亡和弹药数量。

## 安装

1. 游戏版本：*Gunner, HEAT, PC!*，已安装 **MelonLoader 0.6.2（net35 版）**。
2. 把 `bin/Release/RepairKit.dll` 复制到：

   ```
   <游戏根目录>\Bin\Mods\RepairKit.dll
   ```

3. 启动游戏。MelonLoader 会自动加载并在控制台打印 `RepairKit 1.0.0`。
4. 首次运行后会在 `UserData/MelonPreferences.cfg` 中生成 `[RepairKit]` 配置段。

## 配置

| 键 | 默认值 | 说明 |
|---|---|---|
| `RepairKey` | `J` | 维修按键。填 Unity `KeyCode` 名称，如 `J`、`K`、`F5`、`Keypad0`。 |
| `RepairDuration` | `30` | 维修时长（秒）。最小 1 秒。 |
| `RepairHealthPercent` | `70` | 维修后部件血量百分比，范围 1–100。 |
| `ShowHud` | `true` | 是否在屏幕下方显示维修进度条与结果提示。 |

配置项在每次使用时按键名现读，改完配置**无需重启**（`MelonPreferences.cfg` 存盘后
下一次按键即生效，维修中的剩余时间不会变）。

## 使用与行为

- **开始**：先把车停稳，再坐在载具里按一次 `RepairKey`。屏幕下方出现读条，倒计时结束自动完成。
- **完成**：所有受损部件血量补到 `RepairHealthPercent`；比该值更高的部件不会被降低。
  随后清掉各系统的“已损坏”状态标记并重新启用相应功能。
- **中途取消**：维修中再按一次同一个键，立即退出维修，**不获得任何维修效果**，
  车辆原有状态（含手刹、断履带等）原样恢复。
- **受到伤害**：维修期间本车任一非乘员部件血量下降，立刻中断维修，无维修效果。
- **无次数限制、无冷却**：随时可以再修。
- **无法维修的情况**：
  - 载具已被判定摧毁；
  - 已弃车；
  - 车组全部失能；
  - 车辆仍在行驶（水平速度高于约 0.5 m/s，即 1.8 km/h）；
  - 不在任何载具内。

维修期间：

- 无法移动（驾驶输入被拦截，手刹强制拉紧，定速巡航关闭）；
- 无法开火（主炮、导弹、通用武器系统的开火入口全部拦截）；
- 炮塔仍可正常转动瞄准，方便观察周围。

## 代码结构

| 文件 | 职责 |
|---|---|
| `Main.cs` | MelonMod 入口、配置项定义、场景回调。 |
| `RepairSession.cs` | 状态机：按键、进度、移动锁、伤害检测、取消/恢复、HUD。 |
| `RepairApplier.cs` | 真正改数据：血量、底盘/传动/炮塔/火控/武器/瞄具/供弹的损坏标记。 |
| `Patches.cs` | Harmony 补丁：开火封锁 + 在 `Awake` 后记录炮塔/变速箱原始参数。 |

### 两个实现要点

1. **必须先写血量、再清损坏标记。** `SetHealthPercent` 会触发 `HealthChanged`，
   进而让 `ChassisDamageManager` 把 `_engineDamaged` 等标记重新置真。
2. **`DrivableChassis` 才是伤害效果的持久状态持有者。** 只清 `NwhChassis` 的标记不够，
   `SetEngineOn(true)` 会在 `_disabledEngine`/`_killedEngine` 为真时直接返回，
   车会一直动不了。同理 `SlewableTurret.KillPower` 会把转速改成“断电值”、
   `Disable` 会把炮塔刚体阻力改成 0.8，所以要在 `Awake` 后记录原始值并在维修时还原。

## 已知限制

- 不修复外观：车体凹陷、贴图、被炸飞的炮塔（`PoppedTurret`）、弹药架殉爆属于不可逆后果。
- 不复活乘员、不补充弹药（只恢复供弹机构本身）。
- `_loaderDamaged`（装填手受伤导致的装填变慢）属于乘员伤害范畴，不在维修范围内。
- 只对玩家当前所在的载具有效，不会自动修理队友 AI。

## 重新构建

需要 .NET SDK 与本机已安装的游戏：

```bash
dotnet build -c Release
```

游戏路径默认 `E:\SteamLibrary\steamapps\common\Gunner HEAT PC`，
可用 `/p:GHPC_DIR="..."` 覆盖。项目通过 `BepInEx.AssemblyPublicizer` 把
`Assembly-CSharp` 的私有成员公开，便于直接读写游戏内部字段。
