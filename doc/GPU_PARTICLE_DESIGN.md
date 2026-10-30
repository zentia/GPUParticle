# GPUParticle 设计

目标：一套粒子系统，**同时**能在 ZEngine 和 Unity 里跑，并且在**没有 compute
shader 的机型**上也能跑（GLES3.0 / WebGL2 级）。

约束来源：OSGame 只能用 Unity 的 ParticleSystem，不能用 VFX Graph —— VFX 的
模拟全在 compute kernel 里，而有一部分机型没有 compute shader。Unity 自己的
ParticleSystem 之所以到处能跑，是因为它的模拟在 CPU（`Modules/ParticleSystem/
ParticleSystemGeometryJob.cpp`，SIMD + SoA），GPU 只负责画 billboard。

**首要目标是"用 GPU 换 CPU，优化 Unity 的粒子"** ⇒ 落地形态是 **B**：Unity 侧
也要跑 Tier-1（自己的渲染器 + shader），不再由 ParticleSystem 模拟。
"只在 ZEngine 跑 Tier-1、Unity 仍用 ParticleSystem" 是 **A**，A 在 Unity 侧一帧
不省，**达不到首要目标**，它只是 B 的前置（容器格式 / 曲线语义 / 导入器）。

**做 B 之前必须先量**：见 `doc/UNITY_MEASUREMENT.md` + `Tools/Unity/
ParticleSystemProfiler.cs`。粒子总计 < 1ms/帧就别做。

## 一、三档能力分级（一个资产格式、一套模拟语义）

| 档 | 手段 | 需要的能力 | 适用 |
|---|---|---|---|
| Tier 0 | CPU 模拟 + billboard（等价 Unity ParticleSystem） | 无 | 保底 / 需要碰撞回调 |
| **Tier 1** | **无状态 GPU：VS 闭式积分** | 实例化属性 + VTF | GLES3.0 / WebGL2 / 无 compute 的设备 |
| Tier 2 | compute（emit/simulate + indirect dispatch） | SSBO + indirect dispatch | 高端桌面 / 旗舰机 |

档位由 RHI 能力谓词选（`RHI::supportsComputeShaders()` 等，见 AGENTS.md §2.43），
**不是**由 `GraphicsAPI` 枚举猜。

## 二、Tier 1 为什么不需要 compute

粒子**没有状态**。出生那一刻烘焙成一条 `ParticleGpuSpawn`（p0 / v0 / a /
spawnTime / life / seed / 曲线下标），此后每帧顶点着色器按闭式解求位置：

```
t   = time - spawnTime
pos = p0 + v0 * t + 0.5 * a * t²
```

由此全部消失：emit / simulate / kickoff 三条 compute pass、alive-dead 链表、
counter buffer、indirect dispatch 参数。

CPU 剩下的活只有"这一帧谁活着"。恒定速率发射器的第 k 个粒子出生在 `k/rate`
秒，所以：

- **发射**：已知 `time`，缺哪几个序号补哪几个。与 dt 无关，不会有浮点累积漂移
  （`carry += rate*dt` 那种写法帧率一变节奏就变）。
- **存活窗口**：`k ∈ ((t-life)*rate, t*rate]`，O(1) 取整，不必遍历。
- **绘制**：实例数是 CPU 已知的，所以 `CmdDrawInstanced(count)` 即可 —— 没有
  indirect dispatch 也照样画。

CPU 开销从 Unity 的 O(存活粒子) 降到 O(新增粒子)，恒定速率下是 O(1)。

## 三、数据通道：一条路径通吃所有后端

出生记录布局刻意做成 **4 × vec4**（64 B），两种取址方式共用同一份字节：

- 没有 SSBO 的 GLES3.0 / WebGL2：当**实例属性**（`vertexAttribDivisor`，ES3.0 core）。
  窗口在环形缓冲里会绕回，适配器要么把缓冲写两遍，要么在 VS 里取模。
- Vulkan / DX12 / Metal：SSBO 或 VTF 纹理，VS 里 `slot = (first + instanceID) % capacity`。

曲线 / 渐变：烘焙成图集纹理，VS 用 `texelFetch` 采样。ES3.0 / WebGL2 保证
`MAX_VERTEX_TEXTURE_IMAGE_UNITS >= 16`，顶点纹理取样是 core，不需要扩展。

## 四、core / adapter 边界（照 GPUSpine）

```
Libraries/GPUParticle/
├─ Runtime/GPUParticle/     # std-only 核心，不碰 RHI
│   ParticleGpuFormat.h     # 容器格式，尺寸全部 static_assert 钉住
│   ParticleGpuCurves.*     # 曲线 / 渐变（语义对齐 Unity）
│   ParticleGpuEmitter.*    # 发射调度：O(1) 存活窗口
│   ParticleGpuBatch.*      # 拼包 + VS 的 CPU 镜像
│   ParticleGpuWrite.*      # 容器写出（烘焙侧，ZEngine / Unity 共用一份）
├─ Tests~/
└─ CMakeLists.txt           # GPUParticle::Core，PIC ON
```

留在引擎里的只有两层薄适配器：ZEngine 侧 `Function/Particle/ParticleManager.cpp`
（IEngineSystem → 调 core）与 `Function/Render/Passes/ParticlePass.cpp`（RHI：
建缓冲 / 上传 / 录绘制）。着色器公式抽成 `engine/shader/include/particle_sim.h`
（第 4 步），与 `spine_skinning.h` 同构：一份文件供 GLSL / HLSL / Unity 三消费。

## 五、跨语言一致性

`ParticleGpuRandom01` / `ParticleGpuSpawnSeed` 用 lowbias32：只要 uint32 溢出是
wrap 的，任何语言都能复现（C# 侧必须 `unchecked`）。种子掩码到 24 位，因为它的
载体是 `ParticleGpuSpawn::seed`（float —— 出生记录要走实例属性通道，那条通道没有
uint），float 只能精确表示到 2^24。

## 六、C# 镜像已删（2026-10）：烘焙器改成引擎内 C++

曾经有一份 `Runtime/CSharp/`（netstandard2.1 镜像，零 Unity API，整目录可拷进
Unity Assets）+ 跨语言对拍（4622 项全部一致）。**整目录已删除**，理由是路线变了：

1. Unity 侧不再走 C#：核心 C++ 直接编进引擎（`External/GPUParticle/`），
   `Modules/GPUParticle/` 直接调它。C# 镜像不在任何 ship 路径上。
2. 烘焙器（Unity 导入器）原本打算用 C# 写在 Unity Editor —— 而它必须"复刻"
   Unity 的曲线/渐变语义，才需要对拍去兜底。现在烘焙器改成**引擎内 C++**
   （`Modules/GPUParticle/GPUParticleBaker.cpp`），读的就是
   `ParticleSystemCurves.cpp` / `ParticleSystemGradients.cpp` **同一份代码**，
   语义天然对齐，"复刻 + 对拍"这整层需求消失。
3. 于是核心从 3 份拷贝（ZEngine C++ / Unity C++ / C#）降回 2 份（且是同一份
   文件的两个副本，靠 External/README.md 的"改上游"约定维护）。

留下的教训（对拍期间踩到、仍然有效）：

- **出生位置不能逐位比**：它经过 sqrt 归一化，单精度 sqrt 的跨平台实现允许
  1 ulp，要比就用 1e-6 容差。
- **所有 uint 运算必须显式 unchecked**：随机器 `ParticleGpuRandom01` 依赖 uint
  溢出 wrap；一旦开了溢出检查就不是"换个随机序列"而是抛异常——那是静默的视觉
  差异，很难查。这条对将来任何移植（Rust / TS / WASM）都成立。

## 七、还没做

- Unity 导入器（烘焙器）：把 ParticleSystem 的模块参数（Main / Shape / Velocity /
  ColorOverLifetime / SizeOverLifetime / Noise / Collision / Trails）烘焙成容器。
  **位置已定**：`Modules/GPUParticle/GPUParticleBaker.cpp`——**引擎内 C++**，不是
  C#（见 §六）。曲线语义直接复用 `ParticleSystemCurves.cpp` /
  `ParticleSystemGradients.cpp`，不需要复刻。容器写出走 core 的
  `ParticleGpuWrite`（ZEngine 与 Unity 共用同一份 writer，字节必然一致）。
- 着色器公式层 `engine/shader/include/particle_sim.h`（与 `spine_skinning.h`
  同构：一份文件供 GLSL / HLSL / Unity 三消费）+ 对应的 GPU 侧对拍。
- 碰撞：VS 可以采深度做"一次性贴合"，但不能回写。真碰撞要 Tier 0 / Tier 2，
  所以资产在 cook 期要声明所需 feature 并标出最低档位，设备不满足时降级而不是
  整个特效消失。
