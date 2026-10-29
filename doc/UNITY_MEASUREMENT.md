# 上真机量测：Unity 粒子到底吃了多少 CPU

**在做 B（把粒子搬到 GPU）之前先量。** 目标说清楚了是"用 GPU 换 CPU 优化 Unity
的粒子"，那么第一步不是写代码，是确认粒子到底占多少帧时间——占 0.3ms 的话这套
东西全做完也看不出来。

工具：`Tools/Unity/ParticleSystemProfiler.cs`。

---

## 一、怎么量

1. 把 `ParticleSystemProfiler.cs` 拷进工程
   （OSGame：`Client_proj/Program/GameScripts/Assets/Scripts/`）。
2. 挂到场景里一个常驻的 Debug 根节点上，设 `reportIntervalSeconds = 5`。
3. **必须 Development Build**：Build Settings 勾 `Development Build` +
   `Autoconnect Profiler`。`Recorder` 在非 dev 包里取不到 marker（脚本会打
   "Recorder 全为 0" 的警告提醒你）。
4. 装到**那台没有 compute shader 的最低端机型**上——就是我们做这件事的前提机型。
5. 跑最重的场景（团战 / 大招 / 城场景，哪个卡跑哪个），站桩跑够 30 秒。
6. 取 log，或 `adb pull` `persistentDataPath/gpuparticle_profile_*.txt`。

> Deep Profile **不要开**（会把每个方法的开销放大到不可读）。
> Profiler 连不连都行，脚本自己用 `Recorder` 抓，不依赖 Profiler 窗口。

---

## 二、抓的是什么（marker 名来自 Unity C++ 源码实测）

名字是从 `Modules/ParticleSystem/**` 的 `PROFILER_INFORMATION` 一行行捞出来的，
不是猜的。

### 转成 Tier-1 后能整体省掉的

| Marker | 是什么 |
|---|---|
| `ParticleSystem.Update` / `Update2` / `EndUpdateAll` | 主线程调度 |
| `ParticleSystem.Simulate` / `UpdateJob` | job 线程上的积分 |
| `ParticleSystem.GeometryJob` | **生成顶点流**——每帧重写整个存活集合，通常是大头 |
| `ParticleSystem.ScheduleGeometryJobs` / `WaitForPreMapping` | 几何 job 的调度与等待 |
| `ParticleSystem.TrailGeometryJob` | 拖尾 ribbon 几何（CPU 生成） |
| `ParticleSystem.InstancedMeshJob` | mesh 粒子的实例数据 |
| `ParticleSystem.Sort` | 排序 |
| `ParticleSystem.WaitForPreviousRenderingToFinish` | 主线程等上一帧渲染完成（粒子特有的停顿） |

### 省不掉的

| Marker | 是什么 |
|---|---|
| `ParticleSystem.Draw` / `DrawSystem` | 提交绘制——换 Tier-1 之后仍然要画 |

### 模块级（决定"哪些模块必须自己实现"）

`ShapeModule` / `VelocityOverLifetimeModule` / `ClampVelocityModule` /
`InheritVelocityModule` / `ForceModule` / `ExternalForcesModule` /
`NoiseModule` / `ColorOverLifetimeModule` / `ColorByVelocityModule` /
`SizeOverLifetimeModule` / `SizeOverVelocityModule` /
`RotationOverLifetimeModule` / `RotationByVelocityModule` /
`CollisionModule` / `TriggerModule` / `TrailModule` /
`TextureSheetModule` / `LightsModule` / `CustomDataModule` /
`LifetimeByEmitterSpeedModule`

---

## 三、判定线

| 粒子总计 | 结论 |
|---|---|
| **< 1 ms/帧** | **别做**。做了也看不出来，去查别的热点。 |
| 1–3 ms/帧 | 边际。先做第四节那几件便宜的，再看。 |
| **> 3 ms/帧** | **值得做**。 |

再看第二个指标——脚本里"B/C 分类"的粒子数占比：

- **可直接转** + **要额外做** 的粒子数合计占 **> 70%** → B 有意义；
- **不可转**（碰撞 / 触发 / 子发射器）占比高 → B 覆盖不到主要开销，先别做。

> OSGame 实测前提（2026-10-29）：碰撞几乎不用 ⇒ 这一档应该接近 0，好消息；
> 但 Trails / Noise / 3D 旋转 / mesh 粒子用得多 ⇒ 大头会落在"要额外做"这一档。

---

## 四、可转性表（按 OSGame 的实际用法）

| 特性 | 用不用 | Tier-1 能不能做 | 说明 |
|---|---|---|---|
| 基础运动（初速 / 常加速度 / 生命 / 尺寸 / 颜色） | 用 | ✅ 原生 | 闭式解 `p0 + v0*t + 0.5*a*t²` |
| Shape（发射形状） | 用 | ✅ | 出生时烘焙进 `p0`，发射形状只影响出生点 |
| Velocity / Force / Color / Size over Lifetime | 用 | ✅ | 只依赖归一化年龄，曲线烘成图集 |
| **Trails** | **用得多** | ✅ **可解析** | 轨迹是闭式函数，`t, t-Δ, t-2Δ…` 求值即拖尾顶点；**不需要历史缓冲**，反而比 Unity 的 CPU ribbon 更省 |
| **3D 旋转** | **用得多** | ✅ **可解析** | 角速度在出生时按 min-max 曲线定死 ⇒ 每粒子常量 ⇒ `angle = ω*t` |
| **Noise** | **用得多** | ⚠️ **只能近似** | Unity 的 noise 是随时间的力场积分，需要状态，闭式解做不了；只能退成 VS 里的噪声位移（便宜、看着像，但不逐帧等价）。**要不要这个近似必须美术点头** |
| **mesh 粒子** | **用得多** | ⚠️ 有代价 | 走实例化路径（每批有上限，Unity 的 billboard 批上限是 16384 个 quad，实例批远小于此）。数量不大时可接受 |
| Collision | 几乎不用 | ❌ | 无状态 ⇒ 无法碰撞、无法回写 |
| Triggers / Sub Emitters | 不用（先不考虑） | ❌ | 需要 CPU 侧事件 |

**结论**：OSGame 的用法里，真正"做不了"的只有 Noise 的保真度和 mesh 粒子的批次
限制，其余都能做——Trails 和 3D 旋转反而比 Unity 现在的实现更省（原来是 CPU
ribbon + CPU 角速度积分）。

---

## 五、报告怎么读

```
可省（模拟+几何生成+排序）: 2.140 ms/帧     ← 这是 Tier-1 能拿掉的部分
省不掉（提交绘制）        : 0.310 ms/帧
>>> 粒子总计约 2.450 ms/帧（占 14.7% 帧时间）

可直接转 Tier-1 :  128 个 /  41230 粒子 (62%)
要额外做        :   41 个 /  21800 粒子 (33%)   ← trail/noise/旋转/mesh
转不了          :    2 个 /    310 粒子 (0.5%)
```

上例：粒子 2.45ms/帧、可转覆盖 95% ⇒ **值得做**。

单项大头里如果 `ParticleSystem.GeometryJob` > 1ms，那是最典型的"该搬 GPU"信号：
它代表每帧在 CPU 上重写整个存活集合的顶点流（约 128–192 B/粒子/帧），而 Tier-1
的每帧上传量与存活数无关（只上传新增的出生记录，64 B/条）。

---

## 六、量出来不值得的话

按投入产出顺序先试这些（都比换架构便宜一个数量级）：

1. **降粒子数 / 降发射率**——通常砍一半视觉几乎无损；
2. **Flipbook 烘焙**——500 粒子的烟换成几十个粒子 + 序列帧，移动端最常用的一招；
3. **关掉最贵的模块**（看报告里模块明细，哪个高先动哪个）；
4. mesh 粒子开 `Enable GPU Instancing`（Unity 自带）；
5. 如果 `WaitForPreviousRenderingToFinish` 很显眼 → 拆成多个小 System / 改
   Simulation Space，有时能直接缓解主线程等待。

---

## 七、量出来值得的话，B 的范围

已具备（本仓库）：容器格式、曲线/渐变语义、随机器、发射调度、闭式解、C++⇄C#
对拍 4622 项。

还差（按依赖顺序）：

1. **Unity 导入器**：ParticleSystem 模块参数 → `.zpgp`
   （曲线语义要对齐 Unity 的 `ParticleSystemCurves.cpp` / `Gradients`）；
2. **Unity 渲染器**：出生记录烘进纹理 + `CommandBuffer.DrawProcedural`，
   粒子索引 = `vertexID / 6`（不需要 compute、不需要 SSBO、不需要 instancing）；
3. **shader 公式层**：`particle_sim.h`，GLSL / HLSL / ShaderLab 同源；
4. **Trails**：VS 里在 `t, t-Δ…` 上求值生成 ribbon；
5. **3D 旋转**：`angle = ω*t`；
6. **Noise**：VS 噪声位移（近似，要美术验收）；
7. **mesh 粒子**：实例化路径 + 批次上限；
8. **C# 镜像 Burst 化**：`Unity.Collections` + Burst job，不能留托管循环。
