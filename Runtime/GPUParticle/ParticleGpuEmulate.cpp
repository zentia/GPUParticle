#include "ParticleGpuEmulate.h"

#include <cmath>

// 本文件是 shader/include/particle_sim.h 的**逐行 CPU 版**。
// 两边一旦漂移，症状是"能跑、但粒子不太对"——所以对每一处都写了它对应着色器里的
// 哪一行，改任一边时请对着改另一边。

namespace
{
    // 列主序 4x4 × 齐次点（与 GLSL 的 m * vec4(p, 1) 同一约定）
    void TransformPoint(const float m[16], float x, float y, float z, float out4[4])
    {
        out4[0] = m[0] * x + m[4] * y + m[8] * z + m[12];
        out4[1] = m[1] * x + m[5] * y + m[9] * z + m[13];
        out4[2] = m[2] * x + m[6] * y + m[10] * z + m[14];
        out4[3] = m[3] * x + m[7] * y + m[11] * z + m[15];
    }

    // = particle_curve_locate + particle_curve_lerp（等距键 O(1)）
    bool EvalCurveKeys(const ParticleGpuEmulateInput& in, uint32_t key_offset, uint32_t key_count, float t,
                       float& value)
    {
        if (key_count == 0u)
        {
            value = 1.0f;   // 空曲线 = 常量 1（乘法中性）
            return true;
        }
        if (key_count < 2u)
        {
            if (key_offset >= in.curve_key_count)
                return false;
            value = in.curve_keys[key_offset].value;
            return true;
        }

        float clamped = t;
        if (clamped < 0.0f)
            clamped = 0.0f;
        else if (clamped > 1.0f)
            clamped = 1.0f;

        const float u = clamped * static_cast<float>(key_count - 1u);
        uint32_t i = static_cast<uint32_t>(std::floor(u));
        if (i > key_count - 2u)
            i = key_count - 2u;
        float frac = u - static_cast<float>(i);
        if (frac > 1.0f)
            frac = 1.0f;

        // 越界即报错：GPU 上这是"读到邻居的曲线"，不报错
        if (key_offset + i + 1u >= in.curve_key_count)
            return false;

        const float a = in.curve_keys[key_offset + i].value;
        const float b = in.curve_keys[key_offset + i + 1u].value;
        value = a + (b - a) * frac;
        return true;
    }

    // = particle_curve_range_eval
    bool EvalCurveRange(const ParticleGpuEmulateInput& in, uint32_t range_index, float t, float random01,
                        float& value)
    {
        if (range_index >= in.curve_range_count)
            return false;

        const ParticleGpuCurveRange& range = in.curve_ranges[range_index];
        const uint32_t mode = range.mode;

        if (mode == static_cast<uint32_t>(ParticleGpuCurveMode::Constant))
        {
            value = range.constantMin;
            return true;
        }

        // 两条曲线的键是紧挨着的两段：min 在 [keyOffset, +keyCount)，max 紧接着
        const bool two_curves = (mode == static_cast<uint32_t>(ParticleGpuCurveMode::RandomBetweenTwoCurves));
        const uint32_t need = two_curves ? (range.keyCount * 2u) : range.keyCount;
        if (range.keyOffset + need > in.curve_key_count)
            return false;

        float lo = 1.0f;
        float hi = 1.0f;
        if (!EvalCurveKeys(in, range.keyOffset, range.keyCount, t, lo))
            return false;
        if (two_curves && !EvalCurveKeys(in, range.keyOffset + range.keyCount, range.keyCount, t, hi))
            return false;

        if (mode == static_cast<uint32_t>(ParticleGpuCurveMode::Curve))
        {
            value = lo;
            return true;
        }
        if (mode == static_cast<uint32_t>(ParticleGpuCurveMode::RandomBetweenTwoConstants))
        {
            value = range.constantMin + (range.constantMax - range.constantMin) * random01;
            return true;
        }
        if (two_curves)
        {
            value = lo + (hi - lo) * random01;
            return true;
        }

        value = 1.0f;   // 未知模式：与着色器 default 分支一致
        return true;
    }

    // = particle_gradient_color / particle_gradient_alpha（等距键，独立插值）
    bool EvalGradient(const ParticleGpuEmulateInput& in, uint32_t range_index, float t, float out_rgba[4])
    {
        if (range_index >= in.gradient_range_count)
            return false;

        const ParticleGpuGradientRange& g = in.gradient_ranges[range_index];

        float clamped = t;
        if (clamped < 0.0f)
            clamped = 0.0f;
        else if (clamped > 1.0f)
            clamped = 1.0f;

        // 颜色
        if (g.colorKeyCount == 0u)
        {
            out_rgba[0] = out_rgba[1] = out_rgba[2] = 1.0f;
        }
        else
        {
            const float u = clamped * static_cast<float>(g.colorKeyCount - 1u);
            uint32_t i = static_cast<uint32_t>(std::floor(u));
            if (i > g.colorKeyCount - 2u)
                i = g.colorKeyCount - 2u;
            const float frac = u - static_cast<float>(i);
            if (g.colorKeyOffset + i + 1u >= in.gradient_color_key_count)
                return false;
            const ParticleGpuGradientColorKey& a = in.gradient_color_keys[g.colorKeyOffset + i];
            const ParticleGpuGradientColorKey& b = in.gradient_color_keys[g.colorKeyOffset + i + 1u];
            out_rgba[0] = a.r + (b.r - a.r) * frac;
            out_rgba[1] = a.g + (b.g - a.g) * frac;
            out_rgba[2] = a.b + (b.b - a.b) * frac;
        }

        // alpha（独立插值，与 Unity 一致：颜色键与 alpha 键数量可以不同）
        if (g.alphaKeyCount == 0u)
        {
            out_rgba[3] = 1.0f;
        }
        else
        {
            const float u = clamped * static_cast<float>(g.alphaKeyCount - 1u);
            uint32_t i = static_cast<uint32_t>(std::floor(u));
            if (i > g.alphaKeyCount - 2u)
                i = g.alphaKeyCount - 2u;
            const float frac = u - static_cast<float>(i);
            if (g.alphaKeyOffset + i + 1u >= in.gradient_alpha_key_count)
                return false;
            const ParticleGpuGradientAlphaKey& a = in.gradient_alpha_keys[g.alphaKeyOffset + i];
            const ParticleGpuGradientAlphaKey& b = in.gradient_alpha_keys[g.alphaKeyOffset + i + 1u];
            out_rgba[3] = a.a + (b.a - a.a) * frac;
        }
        return true;
    }
}  // namespace

bool ParticleGpuEmulateVertex(const ParticleGpuEmulateInput& in, uint32_t quad_index, uint32_t corner,
                              ParticleGpuEmulatedVertex& out)
{
    // ---- 0) 输入自洽 ----
    if (in.desc == nullptr || in.spawns == nullptr || in.capacity == 0u)
        return false;
    if (in.model == nullptr || in.view_proj == nullptr)
        return false;
    if (corner > 3u || quad_index >= in.window_count)
        return false;

    // ---- 1) 槽位 = particle_ring_slot（纯整数取模）----
    const uint32_t slot = (in.window_first + quad_index) % in.capacity;
    const ParticleGpuSpawn& s = in.spawns[slot];

    // ---- 2) 年龄 = particle_age / particle_is_alive ----
    const float t = in.time - s.spawnTime;
    const float age = t / s.life;   // life > 0：烘焙期 ParticleGpuValidateContainer 已校验
    if (!(age >= 0.0f && age < 1.0f))
        return false;   // 已死：GPU 上这一项根本不该被画（count 不含它）

    // ---- 3) 随机 = particle_hash_u32 / particle_random01 ----
    // seed 是 24 位掩码后的整数，用 float 承载 ⇒ 转 uint 是精确的
    // （见 ParticleGpuSpawnSeed 的注释）
    uint32_t rnd_state = static_cast<uint32_t>(s.seed);
    const float random01 = ParticleGpuRandom01(rnd_state);

    // ---- 4) 尺寸 = particle_curve_range_eval + particle_size ----
    float size_scale = 1.0f;
    if (!EvalCurveRange(in, s.sizeCurve, age, random01, size_scale))
        return false;
    const float size = s.size0 * size_scale;

    // ---- 5) 颜色 = particle_gradient_* ----
    float rgba[4] = {1.0f, 1.0f, 1.0f, 1.0f};
    if (!EvalGradient(in, s.colorGradient, age, rgba))
        return false;

    // ---- 6) 位置 = particle_position（闭式解，不重排）----
    float local[3];
    for (int i = 0; i < 3; ++i)
    {
        local[i] = s.p0[i] + s.v0[i] * t + 0.5f * s.a[i] * t * t;
        local[i] += in.desc->origin[i];
    }

    // ---- 7) 世界：model × 局部点 ----
    float world[4];
    TransformPoint(in.model, local[0], local[1], local[2], world);

    // ---- 8) 公告板角偏移（静态 quad VB 的角序，见头文件注释）----
    const float cx = (corner == 1u || corner == 2u) ? 1.0f : -1.0f;
    const float cy = (corner >= 2u) ? 1.0f : -1.0f;
    const float wx = world[0] + in.cam_right[0] * cx * size + in.cam_up[0] * cy * size;
    const float wy = world[1] + in.cam_right[1] * cx * size + in.cam_up[1] * cy * size;
    const float wz = world[2] + in.cam_right[2] * cx * size + in.cam_up[2] * cy * size;

    // ---- 9) 裁剪：view_proj × 世界点 ----
    float clip[4];
    TransformPoint(in.view_proj, wx, wy, wz, clip);

    out.clip_x = clip[0];
    out.clip_y = clip[1];
    out.clip_z = clip[2];
    out.clip_w = clip[3];

    // ---- 10) UV（与角序一致）----
    out.u = (corner == 1u || corner == 2u) ? 1.0f : 0.0f;
    out.v = (corner >= 2u) ? 1.0f : 0.0f;

    out.r = rgba[0];
    out.g = rgba[1];
    out.b = rgba[2];
    out.a = rgba[3];
    return true;
}
