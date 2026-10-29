// C++ 侧 ParticleGpuEmitter.h/.cpp 的逐行镜像。
//
// 关键（也是 Tier-1 的全部价值所在）：恒定速率的第 k 个粒子出生在 k/rate 秒，
// 所以发射与 dt 无关、存活窗口 O(1) 取整。C# 侧必须算出与 C++ **完全相同**的
// 序号与窗口，否则 Unity 与 ZEngine 两端画出来的粒子数会差一个。

using System;

namespace ZEngine.GPUParticle
{
    public class ParticleGpuEmitterState
    {
        public ParticleGpuSpawn[] Spawns;
        public uint Capacity;
        public ulong Emitted;
        public float Time;
        public bool BurstDone;
        public uint BurstFirst;
        public uint BurstCount;
    }

    public static class ParticleGpuEmitter
    {
        public static bool CheckCapacity(ParticleGpuEmitterDesc desc, out uint required)
        {
            if (desc.Mode == (uint)ParticleGpuEmitMode.Burst)
            {
                required = desc.BurstCount;
                return desc.Capacity >= desc.BurstCount;
            }
            double need = (double)desc.Rate * (double)desc.Lifetime;
            required = (uint)Math.Ceiling(need + 2.0);
            return desc.Capacity >= required;
        }

        public static void Reset(ParticleGpuEmitterState state, ParticleGpuEmitterDesc desc)
        {
            state.Spawns = new ParticleGpuSpawn[desc.Capacity];
            for (int i = 0; i < state.Spawns.Length; ++i)
            {
                state.Spawns[i] = new ParticleGpuSpawn
                {
                    P0 = new float[3],
                    V0 = new float[3],
                    A = new float[3],
                };
            }
            state.Capacity = desc.Capacity;
            state.Emitted = 0UL;
            state.Time = 0.0f;
            state.BurstDone = false;
            state.BurstFirst = 0u;
            state.BurstCount = 0u;
        }

        public static uint RingSlot(ulong particleIndex, uint capacity)
        {
            if (capacity == 0u) return 0u;
            return (uint)(particleIndex % (ulong)capacity);
        }

        public static ParticleGpuSpawn MakeSpawn(ParticleGpuEmitterDesc desc, ulong particleIndex)
        {
            ParticleGpuSpawn s = new ParticleGpuSpawn
            {
                P0 = new float[3],
                V0 = new float[3],
                A = new float[3],
            };

            uint seed = ParticleGpuCurves.SpawnSeed(desc.Seed, particleIndex);

            float spawnTime;
            if (desc.Mode == (uint)ParticleGpuEmitMode.Burst)
            {
                spawnTime = 0.0f;
            }
            else
            {
                // 与 C++ 同序：先 double 除，再转成 float
                spawnTime = (float)((double)particleIndex / (double)(desc.Rate > 0.0f ? desc.Rate : 1.0f));
            }
            s.SpawnTime = spawnTime;

            uint rng = seed;
            float u1 = ParticleGpuCurves.Random01(ref rng);
            float u2 = ParticleGpuCurves.Random01(ref rng);
            float u3 = ParticleGpuCurves.Random01(ref rng);
            float[] dir = { u1 * 2.0f - 1.0f, u2 * 2.0f - 1.0f, u3 * 2.0f - 1.0f };
            float lenSq = dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2];
            // MathF（不是 Math）：Math.Sqrt 走 double，会引入二次舍入 ⇒ 与 C++ 的
            // std::sqrt(float) 差 1 ulp。
            float inv = (lenSq > 1.0e-12f) ? (desc.Spread / MathF.Sqrt(lenSq)) : 0.0f;
            for (int i = 0; i < 3; ++i)
            {
                s.P0[i] = desc.Origin[i] + dir[i] * inv;
            }

            float s1 = ParticleGpuCurves.Random01(ref rng) * 2.0f - 1.0f;
            float s2 = ParticleGpuCurves.Random01(ref rng) * 2.0f - 1.0f;
            float s3 = ParticleGpuCurves.Random01(ref rng) * 2.0f - 1.0f;
            s.V0[0] = desc.Velocity[0] + s1 * desc.VelocitySpread;
            s.V0[1] = desc.Velocity[1] + s2 * desc.VelocitySpread;
            s.V0[2] = desc.Velocity[2] + s3 * desc.VelocitySpread;

            for (int i = 0; i < 3; ++i)
            {
                s.A[i] = desc.Acceleration[i];
            }

            s.Life = desc.Lifetime;
            s.Seed = (float)seed;
            s.Size0 = desc.StartSize;
            s.SizeCurve = (float)desc.SizeCurve;
            s.ColorGradient = (float)desc.ColorGradient;
            s.Flags = 0.0f;
            return s;
        }

        public static ParticleGpuWindow Tick(ParticleGpuEmitterState state, ParticleGpuEmitterDesc desc, float time)
        {
            ParticleGpuWindow window = new ParticleGpuWindow();
            window.Capacity = state.Capacity;
            state.Time = time;

            if (desc.Mode == (uint)ParticleGpuEmitMode.Burst)
            {
                if (!state.BurstDone && desc.BurstCount > 0u)
                {
                    uint n = (desc.BurstCount > state.Capacity) ? state.Capacity : desc.BurstCount;
                    for (uint i = 0u; i < n; ++i)
                    {
                        ulong k = state.Emitted;
                        state.Spawns[RingSlot(k, state.Capacity)] = MakeSpawn(desc, k);
                        ++state.Emitted;
                    }
                    state.BurstFirst = RingSlot(state.Emitted - (ulong)n, state.Capacity);
                    state.BurstCount = n;
                    state.BurstDone = true;
                }
                if (time <= desc.Lifetime && desc.Lifetime > 0.0f)
                {
                    window.First = state.BurstFirst;
                    window.Count = state.BurstCount;
                }
                return window;
            }

            if (desc.Rate <= 0.0f)
            {
                return window;
            }

            double dtime = (double)time;
            double drate = (double)desc.Rate;

            double scheduledExact = dtime * drate;
            ulong scheduled = (scheduledExact < 0.0) ? 0UL : ((ulong)Math.Floor(scheduledExact) + 1UL);

            if (scheduled > state.Emitted)
            {
                ulong missing = scheduled - state.Emitted;
                ulong skip = (missing > state.Capacity) ? (missing - state.Capacity) : 0UL;
                ulong start = state.Emitted + skip;
                for (ulong k = start; k < scheduled; ++k)
                {
                    state.Spawns[RingSlot(k, state.Capacity)] = MakeSpawn(desc, k);
                }
                state.Emitted = scheduled;
            }

            double lo = (dtime - (double)desc.Lifetime) * drate;
            ulong kMin = 0UL;
            if (lo > 0.0)
            {
                kMin = (ulong)Math.Floor(lo) + 1UL;
            }

            if (scheduled == 0UL)
            {
                window.Count = 0u;
                return window;
            }
            ulong kMax = (state.Emitted < scheduled) ? (state.Emitted - 1UL) : (scheduled - 1UL);

            if (state.Emitted > state.Capacity)
            {
                ulong firstKept = state.Emitted - state.Capacity;
                if (kMin < firstKept) kMin = firstKept;
            }
            if (kMax + 1UL < kMin)
            {
                window.Count = 0u;
                return window;
            }
            ulong count = kMax - kMin + 1UL;
            if (count > state.Capacity) count = state.Capacity;

            window.First = RingSlot(kMin, state.Capacity);
            window.Count = (uint)count;
            return window;
        }
    }
}
