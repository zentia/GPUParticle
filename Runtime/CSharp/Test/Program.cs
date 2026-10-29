// 跨语言对拍：C++（Tests~/ParticleGpuDumpTest）落盘，C# 用**自己的实现**重算，
// 逐字段比对。任何一边改了公式 / 布局 / 随机器顺序而另一边没跟上，这里就红。
//
// 为什么逐位比而不是"看着差不多"：粒子在两端要长得一模一样，而尺寸/颜色是在
// 曲线上取的，曲线的一个 ulp 差异会被逐帧放大成肉眼可见的抖动。

using System;
using System.IO;
using ZEngine.GPUParticle;

namespace ZEngine.GPUParticle.Test
{
    public static class Program
    {
        const uint DumpMagic = 0x5A504744u;   // 'ZPGD'
        const uint DumpVersion = 1u;

        // 与 C++ 侧 Tests~/ParticleGpuDumpTest.cpp 的夹具**必须逐值一致**
        static readonly ParticleGpuCurveKey[] Keys =
        {
            new ParticleGpuCurveKey { Time = 0.0f, Value = 0.0f },
            new ParticleGpuCurveKey { Time = 0.5f, Value = 2.0f },
            new ParticleGpuCurveKey { Time = 1.0f, Value = 0.0f },
        };
        static readonly ParticleGpuCurveKey[] PairKeys =
        {
            new ParticleGpuCurveKey { Time = 0.0f, Value = 1.0f },
            new ParticleGpuCurveKey { Time = 1.0f, Value = 1.0f },
            new ParticleGpuCurveKey { Time = 0.0f, Value = 5.0f },
            new ParticleGpuCurveKey { Time = 1.0f, Value = 5.0f },
        };
        static readonly ParticleGpuGradientColorKey[] ColorKeys =
        {
            new ParticleGpuGradientColorKey { Time = 0.0f, R = 1.0f, G = 0.0f, B = 0.0f },
            new ParticleGpuGradientColorKey { Time = 1.0f, R = 0.0f, G = 0.0f, B = 1.0f },
        };
        static readonly ParticleGpuGradientAlphaKey[] AlphaKeys =
        {
            new ParticleGpuGradientAlphaKey { Time = 0.0f, A = 1.0f },
            new ParticleGpuGradientAlphaKey { Time = 1.0f, A = 0.0f },
        };

        static int failures = 0;
        static int checks = 0;

        static void Fail(string what, string detail)
        {
            ++failures;
            Console.WriteLine($"[FAIL] {what}: {detail}");
        }

        static void Exact(float got, float want, string what)
        {
            ++checks;
            if (BitConverter.SingleToInt32Bits(got) != BitConverter.SingleToInt32Bits(want))
            {
                Fail(what, $"got {got:R} want {want:R} (bits {BitConverter.SingleToInt32Bits(got)} vs {BitConverter.SingleToInt32Bits(want)})");
            }
        }

        // 出生位置经过 sqrt 归一化：C++ 用 std::sqrt(float)，C# 用 MathF.Sqrt，
        // 单精度 sqrt 的跨平台实现允许 1 ulp 差异 ⇒ 这里给 1e-6 而不是逐位。
        static void Near(float got, float want, float tol, string what)
        {
            ++checks;
            if (Math.Abs(got - want) > tol)
            {
                Fail(what, $"got {got:R} want {want:R} (tol {tol})");
            }
        }

        static void ExactU32(uint got, uint want, string what)
        {
            ++checks;
            if (got != want)
            {
                Fail(what, $"got {got} want {want}");
            }
        }

        public static int Main(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : ".";
            string path = Path.Combine(dir, "gpuparticle_dump.bin");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[FAIL] 找不到夹具 {path}");
                return 1;
            }

            using (BinaryReader r = new BinaryReader(File.Open(path, FileMode.Open, FileAccess.Read)))
            {
                uint magic = r.ReadUInt32();
                uint version = r.ReadUInt32();
                uint descSize = r.ReadUInt32();
                uint spawnSize = r.ReadUInt32();
                uint spawnCount = r.ReadUInt32();
                uint windowCount = r.ReadUInt32();
                uint sampleCount = r.ReadUInt32();
                _ = r.ReadUInt32();   // reserved

                if (magic != DumpMagic)
                {
                    Console.WriteLine($"[FAIL] magic 不对：{magic:X} != {DumpMagic:X}");
                    return 1;
                }
                if (version != DumpVersion)
                {
                    Console.WriteLine($"[FAIL] version 不对：{version}");
                    return 1;
                }
                // 跨语言布局校验：C++ 的 sizeof 与 C# 的常量必须一致
                ExactU32(descSize, ParticleGpuEmitterDesc.Size, "sizeof(EmitterDesc) 两端一致");
                ExactU32(spawnSize, ParticleGpuSpawn.Size, "sizeof(Spawn) 两端一致");

                // ---- 发射器描述 ----
                ParticleGpuEmitterDesc desc = new ParticleGpuEmitterDesc
                {
                    Origin = new float[3],
                    Velocity = new float[3],
                    Acceleration = new float[3],
                    Reserved = new float[2],
                };
                desc.Origin[0] = r.ReadSingle(); desc.Origin[1] = r.ReadSingle(); desc.Origin[2] = r.ReadSingle();
                desc.Spread = r.ReadSingle();
                desc.Velocity[0] = r.ReadSingle(); desc.Velocity[1] = r.ReadSingle(); desc.Velocity[2] = r.ReadSingle();
                desc.VelocitySpread = r.ReadSingle();
                desc.Acceleration[0] = r.ReadSingle(); desc.Acceleration[1] = r.ReadSingle(); desc.Acceleration[2] = r.ReadSingle();
                desc.Lifetime = r.ReadSingle();
                desc.StartSize = r.ReadSingle();
                desc.Rate = r.ReadSingle();
                desc.Reserved[0] = r.ReadSingle(); desc.Reserved[1] = r.ReadSingle();
                desc.Capacity = r.ReadUInt32();
                desc.Mode = r.ReadUInt32();
                desc.BurstCount = r.ReadUInt32();
                desc.Seed = r.ReadUInt32();
                desc.SizeCurve = r.ReadUInt32();
                desc.ColorGradient = r.ReadUInt32();
                desc.Flags = r.ReadUInt32();
                desc.ReservedU = r.ReadUInt32();

                // ---- 出生记录：用 C# 自己的实现重算 ----
                for (uint i = 0; i < spawnCount; ++i)
                {
                    float[] f = new float[16];
                    for (int k = 0; k < 16; ++k) f[k] = r.ReadSingle();

                    ParticleGpuSpawn mine = ParticleGpuEmitter.MakeSpawn(desc, (ulong)i);
                    string tag = $"spawn[{i}]";
                    Near(mine.P0[0], f[0], 1e-6f, tag + ".p0.x");
                    Near(mine.P0[1], f[1], 1e-6f, tag + ".p0.y");
                    Near(mine.P0[2], f[2], 1e-6f, tag + ".p0.z");
                    Exact(mine.SpawnTime, f[3], tag + ".spawnTime");
                    Exact(mine.V0[0], f[4], tag + ".v0.x");
                    Exact(mine.V0[1], f[5], tag + ".v0.y");
                    Exact(mine.V0[2], f[6], tag + ".v0.z");
                    Exact(mine.Life, f[7], tag + ".life");
                    Exact(mine.A[0], f[8], tag + ".a.x");
                    Exact(mine.A[1], f[9], tag + ".a.y");
                    Exact(mine.A[2], f[10], tag + ".a.z");
                    Exact(mine.Seed, f[11], tag + ".seed");
                    Exact(mine.Size0, f[12], tag + ".size0");
                    Exact(mine.SizeCurve, f[13], tag + ".sizeCurve");
                    Exact(mine.ColorGradient, f[14], tag + ".colorGradient");
                    Exact(mine.Flags, f[15], tag + ".flags");
                }

                // ---- 存活窗口：按同样顺序 Tick ----
                ParticleGpuEmitterState st = new ParticleGpuEmitterState();
                ParticleGpuEmitter.Reset(st, desc);
                for (uint w = 0; w < windowCount; ++w)
                {
                    float t = r.ReadSingle();
                    uint first = r.ReadUInt32();
                    uint count = r.ReadUInt32();
                    uint capacity = r.ReadUInt32();

                    ParticleGpuWindow mine = ParticleGpuEmitter.Tick(st, desc, t);
                    string tag = $"window(t={t:R})";
                    ExactU32(mine.First, first, tag + ".first");
                    ExactU32(mine.Count, count, tag + ".count");
                    ExactU32(mine.Capacity, capacity, tag + ".capacity");
                }

                // ---- 曲线 / 渐变 ----
                ParticleGpuGradientRange gradRange = new ParticleGpuGradientRange
                {
                    ColorKeyOffset = 0,
                    ColorKeyCount = 2,
                    AlphaKeyOffset = 0,
                    AlphaKeyCount = 2,
                };
                float[] grad = new float[4];
                for (uint s = 0; s < sampleCount; ++s)
                {
                    float t = r.ReadSingle();
                    float rnd = r.ReadSingle();
                    uint mode = r.ReadUInt32();
                    float curve = r.ReadSingle();
                    grad[0] = r.ReadSingle(); grad[1] = r.ReadSingle();
                    grad[2] = r.ReadSingle(); grad[3] = r.ReadSingle();

                    bool twoCurves = (mode == (uint)ParticleGpuCurveMode.RandomBetweenTwoCurves);
                    ParticleGpuCurveRange range = new ParticleGpuCurveRange
                    {
                        Mode = mode,
                        KeyOffset = 0,
                        KeyCount = twoCurves ? 2u : 3u,
                        ConstantMin = 2.0f,
                        ConstantMax = 6.0f,
                    };
                    float mine = ParticleGpuCurves.EvaluateCurveRange(range, twoCurves ? PairKeys : Keys, t, rnd);
                    Exact(mine, curve, $"curve(mode={mode}, t={t:R}, r={rnd:R})");

                    float[] mineGrad = new float[4];
                    ParticleGpuCurves.EvaluateGradient(gradRange, ColorKeys, AlphaKeys, t, mineGrad);
                    for (int k = 0; k < 4; ++k)
                    {
                        Exact(mineGrad[k], grad[k], $"gradient(mode={mode}, t={t:R})[{k}]");
                    }
                }
            }

            Console.WriteLine(failures == 0
                ? $"[PASS] GPUParticle 跨语言对拍：{checks} 项全部一致"
                : $"[FAIL] GPUParticle 跨语言对拍：{failures} / {checks} 项不一致");
            return failures == 0 ? 0 : 1;
        }
    }
}
