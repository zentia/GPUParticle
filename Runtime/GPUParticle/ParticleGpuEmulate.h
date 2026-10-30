#pragma once

// =====================================================================
// 顶点着色器的 CPU 版（逐行镜像）
// ---------------------------------------------------------------------
// 与 Spine 的 SpineGpuEmulateVertex 同一个思路：着色器里"读到别人的数据"是
// **不报错**的（越界读到邻居字段、曲线取错段、随机序列偏一格），症状只是
// "看起来不太对"，查起来是几天的量级。所以把着色器在 CPU 上逐行重写一遍：
//   * 任何一次寻址越界 ⇒ 返回 false（GPU 上那是不报错的读到别人的数据）；
//   * 于是"公式层改了 / 布局改了 / 取址改了"三种漂移都能在**没有 GPU、
//     没有引擎**的环境里被抓到。
//
// ⚠️ 本文件与 shader/include/particle_sim.h 是**同一条纪律的两端**：
//    改了那边的公式，这里必须同步改；反之亦然。两边都刻意不重排运算顺序。
//
// 谁用它：
//   * 单测：CPU 算一遍 → 与 GPU（真机 / 渲染测试）逐顶点比；
//   * 烘焙 / 离线验证：不出图也能确认"这个容器 + 这个时刻"画出来是对的。
// =====================================================================

#include "ParticleGpuEmitter.h"
#include "ParticleGpuFormat.h"

#include <cstdint>

// 一次"画这一帧"的全部输入。全是**只读**的视图，调用方负责生命周期。
struct ParticleGpuEmulateInput
{
    // ---- 出生记录（环形）----
    const ParticleGpuSpawn* spawns {nullptr};
    uint32_t capacity {0};
    uint32_t window_first {0};   // ParticleGpuWindow::first
    uint32_t window_count {0};   // ParticleGpuWindow::count（0 ⇒ 这一帧没得画）

    // ---- 发射器描述（尺寸 / 曲线的下标在这里）----
    const ParticleGpuEmitterDesc* desc {nullptr};

    // ---- 曲线 / 渐变（容器里的表）----
    const ParticleGpuCurveRange* curve_ranges {nullptr};
    uint32_t curve_range_count {0};
    const ParticleGpuCurveKey* curve_keys {nullptr};
    uint32_t curve_key_count {0};
    const ParticleGpuGradientRange* gradient_ranges {nullptr};
    uint32_t gradient_range_count {0};
    const ParticleGpuGradientColorKey* gradient_color_keys {nullptr};
    uint32_t gradient_color_key_count {0};
    const ParticleGpuGradientAlphaKey* gradient_alpha_keys {nullptr};
    uint32_t gradient_alpha_key_count {0};

    // ---- 摆放 ----
    float time {0.0f};
    const float* model {nullptr};       // 发射器局部 → 世界，列主序 16 float
    const float* view_proj {nullptr};   // 世界 → 裁剪，列主序 16 float
    float cam_right[3] {1.0f, 0.0f, 0.0f};   // 公告板的"右"（世界空间，单位向量）
    float cam_up[3] {0.0f, 1.0f, 0.0f};      // 公告板的"上"
};

struct ParticleGpuEmulatedVertex
{
    float clip_x {0.0f};
    float clip_y {0.0f};
    float clip_z {0.0f};
    float clip_w {1.0f};
    float u {0.0f};
    float v {0.0f};
    float r {1.0f};
    float g {1.0f};
    float b {1.0f};
    float a {1.0f};
};

// 模拟"第 quad_index 个 quad 的第 corner 个角"（= vertexID / 4 与 % 4）。
// 返回 false 只可能是输入不自洽（下标越界 / 该粒子已死 / 表缺失）。
//
// 角序与静态 quad VB 一致（Modules/GPUParticle 的 GPUParticleDrawResources）：
//   0 = (-1,-1) uv(0,0)   1 = ( 1,-1) uv(1,0)
//   2 = ( 1, 1) uv(1,1)   3 = (-1, 1) uv(0,1)
// 改了那边的角序，这里必须跟着改。
bool ParticleGpuEmulateVertex(const ParticleGpuEmulateInput& in, uint32_t quad_index, uint32_t corner,
                              ParticleGpuEmulatedVertex& out);
