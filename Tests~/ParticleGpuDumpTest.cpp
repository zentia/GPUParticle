// =====================================================================
// 跨语言对拍的 C++ 侧：把夹具落盘，交给 C# 逐字段复算比对
// ---------------------------------------------------------------------
// 不直接 add_test：它是夹具生成器，真正的测试是
// "cmake -P Runtime/CSharp/Test/RunParityTest.cmake"（先出字节、再验字节）。
//
// 夹具字节流（小端，按逻辑顺序交错）：
//   头：magic 'ZPGD' / version / sizeof(EmitterDesc) / sizeof(Spawn) /
//       spawnCount / windowCount / sampleCount / reserved
//   1 份 EmitterDesc（16 x f32 + 8 x u32）
//   spawnCount x Spawn（16 x f32）
//   windowCount x { f32 time, u32 first, u32 count, u32 capacity }
//   sampleCount x { f32 t, f32 random01, u32 mode, f32 curve, f32 grad[4] }
// =====================================================================

#include "GPUParticle/ParticleGpuBatch.h"
#include "GPUParticle/ParticleGpuCurves.h"
#include "GPUParticle/ParticleGpuEmitter.h"
#include "GPUParticle/ParticleGpuFormat.h"

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace
{
    constexpr uint32_t kDumpMagic = 0x5A504744u;  // 'ZPGD'
    constexpr uint32_t kDumpVersion = 1u;

    std::vector<uint8_t> g_bytes;

    void PushU32(uint32_t v)
    {
        uint8_t b[4] = {static_cast<uint8_t>(v & 0xFFu),
                        static_cast<uint8_t>((v >> 8) & 0xFFu),
                        static_cast<uint8_t>((v >> 16) & 0xFFu),
                        static_cast<uint8_t>((v >> 24) & 0xFFu)};
        g_bytes.insert(g_bytes.end(), b, b + 4);
    }

    void PushF32(float v)
    {
        uint32_t bits = 0u;
        std::memcpy(&bits, &v, 4);
        PushU32(bits);
    }

    // 曲线 / 渐变夹具：必须与 C# 侧 Test/Program.cs 的 Fixture 完全一致
    // （改一边忘另一边 → 对拍立刻红，这正是它存在的意义）。
    const ParticleGpuCurveKey kKeys[3] = {{0.0f, 0.0f}, {0.5f, 2.0f}, {1.0f, 0.0f}};
    const ParticleGpuCurveKey kPairKeys[4] = {{0.0f, 1.0f}, {1.0f, 1.0f}, {0.0f, 5.0f}, {1.0f, 5.0f}};
    const ParticleGpuGradientColorKey kColorKeys[2] = {{0.0f, 1.0f, 0.0f, 0.0f},
                                                       {1.0f, 0.0f, 0.0f, 1.0f}};
    const ParticleGpuGradientAlphaKey kAlphaKeys[2] = {{0.0f, 1.0f}, {1.0f, 0.0f}};

    ParticleGpuEmitterDesc MakeDesc()
    {
        ParticleGpuEmitterDesc d {};
        d.origin[0] = 1.0f;
        d.origin[1] = 2.0f;
        d.origin[2] = 3.0f;
        d.spread = 0.25f;
        d.velocity[0] = 0.5f;
        d.velocity[1] = 3.0f;
        d.velocity[2] = -1.0f;
        d.velocitySpread = 0.75f;
        d.acceleration[0] = 0.0f;
        d.acceleration[1] = -9.8f;
        d.acceleration[2] = 0.1f;
        d.lifetime = 1.5f;
        d.startSize = 0.75f;
        d.rate = 100.0f;
        d.capacity = 256u;
        d.mode = static_cast<uint32_t>(ParticleGpuEmitMode::ConstantRate);
        d.burstCount = 0u;
        d.seed = 0xC0FFEEu;
        d.sizeCurve = 0u;
        d.colorGradient = 0u;
        d.flags = 0u;
        return d;
    }

    void PushDesc(const ParticleGpuEmitterDesc& d)
    {
        PushF32(d.origin[0]); PushF32(d.origin[1]); PushF32(d.origin[2]);
        PushF32(d.spread);
        PushF32(d.velocity[0]); PushF32(d.velocity[1]); PushF32(d.velocity[2]);
        PushF32(d.velocitySpread);
        PushF32(d.acceleration[0]); PushF32(d.acceleration[1]); PushF32(d.acceleration[2]);
        PushF32(d.lifetime);
        PushF32(d.startSize);
        PushF32(d.rate);
        PushF32(d.reserved[0]); PushF32(d.reserved[1]);
        PushU32(d.capacity);
        PushU32(d.mode);
        PushU32(d.burstCount);
        PushU32(d.seed);
        PushU32(d.sizeCurve);
        PushU32(d.colorGradient);
        PushU32(d.flags);
        PushU32(d.reservedU);
    }

    void PushSpawn(const ParticleGpuSpawn& s)
    {
        PushF32(s.p0[0]); PushF32(s.p0[1]); PushF32(s.p0[2]); PushF32(s.spawnTime);
        PushF32(s.v0[0]); PushF32(s.v0[1]); PushF32(s.v0[2]); PushF32(s.life);
        PushF32(s.a[0]); PushF32(s.a[1]); PushF32(s.a[2]); PushF32(s.seed);
        PushF32(s.size0); PushF32(s.sizeCurve); PushF32(s.colorGradient); PushF32(s.flags);
    }
}  // namespace

int main(int argc, char** argv)
{
    std::string dir = ".";
    for (int i = 1; i < argc; ++i)
    {
        if (std::strcmp(argv[i], "--dump") == 0 && i + 1 < argc)
        {
            dir = argv[++i];
        }
        else if (argv[i][0] != '-' && argv[i][0] != '\0')
        {
            dir = argv[i];
        }
    }
    const std::string path = dir + "/gpuparticle_dump.bin";

    const ParticleGpuEmitterDesc desc = MakeDesc();

    // ---- 出生记录：序号 0..255（C# 侧独立重算）----
    const uint32_t spawn_count = 256u;
    std::vector<ParticleGpuSpawn> spawns;
    spawns.reserve(spawn_count);
    for (uint32_t i = 0u; i < spawn_count; ++i)
    {
        spawns.push_back(ParticleGpuMakeSpawn(desc, static_cast<uint64_t>(i)));
    }

    // ---- 存活窗口：几个时刻（含刚起步、稳态、跨过寿命边界）----
    const float times[8] = {0.0f, 0.017f, 0.25f, 1.0f, 1.5f, 2.0f, 3.7f, 10.0f};
    std::vector<ParticleGpuWindow> windows;
    {
        ParticleGpuEmitterState st {};
        ParticleGpuEmitterReset(st, desc);
        for (float t : times)
        {
            windows.push_back(ParticleGpuEmitterTick(st, desc, t));
        }
    }

    // ---- 曲线 / 渐变采样 ----
    struct Sample
    {
        float t;
        float random01;
        uint32_t mode;
        float curve;
        float grad[4];
    };
    std::vector<Sample> samples;

    ParticleGpuGradientRange grad_range {};
    grad_range.colorKeyOffset = 0u;
    grad_range.colorKeyCount = 2u;
    grad_range.alphaKeyOffset = 0u;
    grad_range.alphaKeyCount = 2u;

    const uint32_t modes[4] = {
        static_cast<uint32_t>(ParticleGpuCurveMode::Constant),
        static_cast<uint32_t>(ParticleGpuCurveMode::Curve),
        static_cast<uint32_t>(ParticleGpuCurveMode::RandomBetweenTwoConstants),
        static_cast<uint32_t>(ParticleGpuCurveMode::RandomBetweenTwoCurves)};
    const float rnds[5] = {0.0f, 0.25f, 0.5f, 0.75f, 1.0f};
    const float ts[5] = {0.0f, 0.25f, 0.5f, 0.9f, 1.0f};

    for (uint32_t m : modes)
    {
        const bool two_curves = (m == static_cast<uint32_t>(ParticleGpuCurveMode::RandomBetweenTwoCurves));
        for (float r : rnds)
        {
            for (float t : ts)
            {
                ParticleGpuCurveRange range {};
                range.mode = m;
                range.keyOffset = 0u;
                range.keyCount = two_curves ? 2u : 3u;   // 两条曲线的模式：每段 2 键
                range.constantMin = 2.0f;
                range.constantMax = 6.0f;

                Sample s {};
                s.t = t;
                s.random01 = r;
                s.mode = m;
                s.curve = ParticleGpuEvaluateCurveRange(range, two_curves ? kPairKeys : kKeys, t, r);
                ParticleGpuEvaluateGradient(grad_range, kColorKeys, kAlphaKeys, t, s.grad);
                samples.push_back(s);
            }
        }
    }

    // ---- 组装字节流 ----
    PushU32(kDumpMagic);
    PushU32(kDumpVersion);
    PushU32(static_cast<uint32_t>(sizeof(ParticleGpuEmitterDesc)));
    PushU32(static_cast<uint32_t>(sizeof(ParticleGpuSpawn)));
    PushU32(spawn_count);
    PushU32(static_cast<uint32_t>(windows.size()));
    PushU32(static_cast<uint32_t>(samples.size()));
    PushU32(0u);

    PushDesc(desc);
    for (const ParticleGpuSpawn& s : spawns)
    {
        PushSpawn(s);
    }
    for (size_t i = 0; i < windows.size(); ++i)
    {
        PushF32(times[i]);
        PushU32(windows[i].first);
        PushU32(windows[i].count);
        PushU32(windows[i].capacity);
    }
    for (const Sample& s : samples)
    {
        PushF32(s.t);
        PushF32(s.random01);
        PushU32(s.mode);
        PushF32(s.curve);
        PushF32(s.grad[0]); PushF32(s.grad[1]); PushF32(s.grad[2]); PushF32(s.grad[3]);
    }

    FILE* f = std::fopen(path.c_str(), "wb");
    if (f == nullptr)
    {
        std::printf("dump: cannot open %s\n", path.c_str());
        return 1;
    }
    const size_t wrote = std::fwrite(g_bytes.data(), 1, g_bytes.size(), f);
    std::fclose(f);
    if (wrote != g_bytes.size())
    {
        std::printf("dump: short write (%zu / %zu)\n", wrote, g_bytes.size());
        return 1;
    }

    std::printf("dump: %u spawns, %zu windows, %zu samples, %zu bytes -> %s\n", spawn_count,
                windows.size(), samples.size(), g_bytes.size(), path.c_str());
    return 0;
}
