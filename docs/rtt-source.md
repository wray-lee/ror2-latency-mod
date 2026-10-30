# RTT 来源与单程延迟 U

反编译源码（只读）：`F:/ror2-decomp/RoR2src`。

## 游戏提供的读数

`F:/ror2-decomp/RoR2src/RoR2.Networking/RttManager.cs`：

| 成员 | 单位 | 含义 |
| --- | --- | --- |
| `RttManager.GetConnectionRTT(NetworkConnection)`（:58） | 秒 | 最近一次 ping 的原始 RTT（`newestRttInSeconds`），未平滑 |
| `RttManager.GetConnectionRTTInMilliseconds(NetworkConnection)`（:67） | 毫秒（uint） | 最近一次 ping 的原始 RTT |
| `RttManager.GetConnectionFrameSmoothedRtt(NetworkConnection)`（:76） | 秒 | 按帧平滑后的 RTT（`frameSmoothedRtt`） |
| `RttManager.GetConnectionFixedSmoothedRtt(NetworkConnection)`（:85） | 秒 | 按 FixedUpdate 平滑后的 RTT（`fixedSmoothedRtt`） |

RTT 由 `HandlePingResponse`（:176）用 `RoR2Application.stopwatch` 计算一次 ping/pong 往返；平滑由 `UpdateFilteredRtt`（定义 :122，调用 :153、:164）完成，平滑窗口 convar `net_rtt_smooth_duration` 默认 `0.1` 秒（:56）。

`F:/ror2-decomp/RoR2src/RoR2.Networking/NetworkManagerSystem.cs`：

| 成员 | 单位 | 含义 |
| --- | --- | --- |
| `NetworkManagerSystem.clientRttFrame`（:592） | 秒 | `GetConnectionRTT` 的镜像（原始值，未平滑） |
| `NetworkManagerSystem.clientRttFixed`（:590） | 秒 | 同上，定点更新路径 |
| `NetworkManagerSystem.filteredClientRttFrame`（:596） | 秒 | `GetConnectionFrameSmoothedRtt` 的镜像（帧平滑） |
| `NetworkManagerSystem.filteredClientRttFixed`（:594） | 秒 | `GetConnectionFixedSmoothedRtt` 的镜像（定点平滑） |

镜像赋值见 :854-855（`UpdateClient`）与 :884-885（`FixedUpdateClient`）。`serverFixedTime`/`serverFrameTime`（:550、:556）把对应的平滑 RTT 加进服务器时间估算。

## 选用的读数

本 mod 取 **`NetworkManagerSystem.singleton.filteredClientRttFrame`**，等价于对当前客户端连接调用 `RttManager.GetConnectionFrameSmoothedRtt(client.connection)`。

理由：

1. 这是客户端自己看到的 RTT，符合本 mod 只在 Remote Client 运行的前提。
2. spec 要求“客户端平滑 RTT”；`clientRttFrame` 名字里虽有 Rtt，但只是最近一次 ping 的原始样本，会被单次抖动放大。`filteredClientRttFrame` 是 `net_rtt_smooth_duration` 窗口内的平滑值。
3. 选帧平滑而不是定点平滑，因为预测/插值在每帧逻辑里消费；`filteredClientRttFixed` 的用途是服务器时间补偿（`serverFixedTime`），曲线更滞后。
4. 直接读 `NetworkManagerSystem` 属性不需要自己管理 `client.connection`；连接建立前属性为 0，适配层对 `singleton` 做空值保护。

## 单程延迟 U = RTT / 2

一次 RTT 包含 Remote Client → Host 与 Host → Remote Client 两段（`HandlePingResponse` 计量的是完整 ping/pong 往返）。在上下行路径对称的假设下，单程延迟 `U = RTT / 2`，这也是 spec P1 的定义，P2/P4/P5 均以 U 为输入。

- 0 RTT → U = 0（测试覆盖）。
- 作为 Host 时没有需要预测的往返，`LatencyEstimator.TryGetOneWayLatency` 返回 false 且不读取 RTT。

## 运行时适配

`src/RoR2LatencyMod/GameLatency.cs` 是唯一的游戏运行时绑定：

- RTT 读数：`NetworkManagerSystem.singleton.filteredClientRttFrame`（秒）。
- 时钟：`UnityEngine.Time.time`。
- 主机判定：`NetworkManagerSystem.singleton.IsHost()`；`isHost` 在 `OnStartHost` 置真、`OnStopHost` 置假（NetworkManagerSystem.cs:1188-1205）。判定以 `Func<bool>` 注入 `SkillPatchInstaller` / `LatencyEstimator`，测试用假谓词，不加载游戏程序集。

## 与 clumsy / `ping` 校准

> 历史遗留：clumsy 双实例方案已不需要；`ping` 读数仍可用于真实房间校准（见 `docs/real-room-test.md`）。下文 clumsy 设置仅为历史参考。

游戏内 `ping` 命令即 `NetworkManagerSystem.CCPing`（NetworkManagerSystem.cs:975）。输出格式：

```
rtt=<毫秒>ms smoothedFrame=<秒> smoothedFixed=<秒>
```

注意：`rtt` 是毫秒，而两个 smoothed 值是**秒**（如 `smoothedFrame=0.15` 表示 150ms）。比对时把 `smoothedFrame × 1000` 与 clumsy 目标 RTT 对照；U 取 `smoothedFrame / 2`。clumsy 的设置方法见 `docs/research-test-setup.md`。
