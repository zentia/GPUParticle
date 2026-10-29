// C++ 侧 ParticleGpuCurves.h/.cpp 的逐行镜像。
//
// 对拍要求**逐位相同**，所以：
//   - 所有 uint 运算必须在 unchecked 里（C# 默认就检查不了，但写清楚，避免有人
//     在工程里开 <CheckForOverflowUnderflow>）；
//   - float 运算不允许被提升成 double：任何 `(float)` 转换都必须显式，不能用
//     `var` 让编译器选 double 重载（Math.Sqrt 是 double，MathF.Sqrt 才是 float）。

using System;

namespace ZEngine.GPUParticle
{
    public static class ParticleGpuCurves
    {
        // lowbias32（Chris Wellons）。只要 uint 溢出是 wrap 的，任何语言都能复现。
        public static uint HashU32(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7feb352du;
                x ^= x >> 15;
                x *= 0x846ca68bu;
                x ^= x >> 16;
                return x;
            }
        }

        public static float Random01(ref uint state)
        {
            state = HashU32(state);
            // 取高 24 位：低位在乘法哈希里质量差，且 float 只有 24 位尾数。
            return (float)(state >> 8) * (1.0f / 16777216.0f);
        }

        // 掩码到 24 位：种子的载体是 ParticleGpuSpawn.Seed（float），float 只能精确
        // 表示到 2^24。不掩码就会在写入时静默截断 ⇒ CPU 与 GPU 拿到不同的种子。
        public static uint SpawnSeed(uint emitterSeed, ulong particleIndex)
        {
            unchecked
            {
                uint h = HashU32(emitterSeed ^ 0x9e3779b9u);
                h = HashU32(h ^ (uint)(particleIndex & 0xFFFFFFFFUL));
                h = HashU32(h ^ (uint)(particleIndex >> 32));
                return h & 0x00FFFFFFu;
            }
        }

        public static float EvaluateCurve(ParticleGpuCurveKey[] keys, uint keyCount, float t)
        {
            if (keys == null || keyCount == 0u)
            {
                return 1.0f;   // 空曲线当常量 1：乘法中性
            }
            float clamped = t;
            if (clamped < 0.0f) clamped = 0.0f;
            else if (clamped > 1.0f) clamped = 1.0f;

            uint i = 0u;
            while (i + 1u < keyCount && keys[i + 1u].Time <= clamped)
            {
                ++i;
            }
            if (i + 1u >= keyCount)
            {
                return keys[i].Value;
            }
            float span = keys[i + 1u].Time - keys[i].Time;
            float frac = (span > 0.0f) ? (clamped - keys[i].Time) / span : 0.0f;
            float a = keys[i].Value;
            float b = keys[i + 1u].Value;
            return a + (b - a) * frac;
        }

        public static float EvaluateCurveRange(ParticleGpuCurveRange range, ParticleGpuCurveKey[] allKeys, float t, float random01)
        {
            switch ((ParticleGpuCurveMode)range.Mode)
            {
                case ParticleGpuCurveMode.Constant:
                    return range.ConstantMin;

                case ParticleGpuCurveMode.Curve:
                    return EvaluateCurve(allKeys, range.KeyCount, t);

                case ParticleGpuCurveMode.RandomBetweenTwoConstants:
                    return range.ConstantMin + (range.ConstantMax - range.ConstantMin) * random01;

                case ParticleGpuCurveMode.RandomBetweenTwoCurves:
                    {
                        float lo = EvaluateCurve(allKeys, range.KeyCount, t);
                        // max 段的键紧接 min 段放，条数相同
                        float hi = EvaluateCurveAt(allKeys, range.KeyOffset + range.KeyCount, range.KeyCount, t);
                        return lo + (hi - lo) * random01;
                    }

                default:
                    return 1.0f;
            }
        }

        // 从 allKeys 的 offset 处取 keyCount 个键求值（RandomBetweenTwoCurves 的 max 段）
        public static float EvaluateCurveAt(ParticleGpuCurveKey[] allKeys, uint offset, uint keyCount, float t)
        {
            if (allKeys == null || keyCount == 0u)
            {
                return 1.0f;
            }
            float clamped = t;
            if (clamped < 0.0f) clamped = 0.0f;
            else if (clamped > 1.0f) clamped = 1.0f;

            uint i = 0u;
            while (i + 1u < keyCount && allKeys[offset + i + 1u].Time <= clamped)
            {
                ++i;
            }
            float a = allKeys[offset + i].Value;
            if (i + 1u >= keyCount)
            {
                return a;
            }
            float span = allKeys[offset + i + 1u].Time - allKeys[offset + i].Time;
            float frac = (span > 0.0f) ? (clamped - allKeys[offset + i].Time) / span : 0.0f;
            float b = allKeys[offset + i + 1u].Value;
            return a + (b - a) * frac;
        }

        public static void EvaluateGradient(ParticleGpuGradientRange range,
                                            ParticleGpuGradientColorKey[] colorKeys,
                                            ParticleGpuGradientAlphaKey[] alphaKeys,
                                            float t,
                                            float[] outRgba)
        {
            float clamped = t;
            if (clamped < 0.0f) clamped = 0.0f;
            else if (clamped > 1.0f) clamped = 1.0f;

            if (range.ColorKeyCount == 0u)
            {
                outRgba[0] = 1.0f; outRgba[1] = 1.0f; outRgba[2] = 1.0f;
            }
            else
            {
                uint i = 0u;
                while (i + 1u < range.ColorKeyCount && colorKeys[range.ColorKeyOffset + i + 1u].Time <= clamped)
                {
                    ++i;
                }
                uint j = (i + 1u < range.ColorKeyCount) ? (i + 1u) : i;
                ParticleGpuGradientColorKey a = colorKeys[range.ColorKeyOffset + i];
                ParticleGpuGradientColorKey b = colorKeys[range.ColorKeyOffset + j];
                float span = b.Time - a.Time;
                float frac = (span > 0.0f) ? (clamped - a.Time) / span : 0.0f;
                outRgba[0] = a.R + (b.R - a.R) * frac;
                outRgba[1] = a.G + (b.G - a.G) * frac;
                outRgba[2] = a.B + (b.B - a.B) * frac;
            }

            if (range.AlphaKeyCount == 0u)
            {
                outRgba[3] = 1.0f;
            }
            else
            {
                uint i = 0u;
                while (i + 1u < range.AlphaKeyCount && alphaKeys[range.AlphaKeyOffset + i + 1u].Time <= clamped)
                {
                    ++i;
                }
                uint j = (i + 1u < range.AlphaKeyCount) ? (i + 1u) : i;
                ParticleGpuGradientAlphaKey a = alphaKeys[range.AlphaKeyOffset + i];
                ParticleGpuGradientAlphaKey b = alphaKeys[range.AlphaKeyOffset + j];
                float span = b.Time - a.Time;
                float frac = (span > 0.0f) ? (clamped - a.Time) / span : 0.0f;
                outRgba[3] = a.A + (b.A - a.A) * frac;
            }
        }
    }
}
