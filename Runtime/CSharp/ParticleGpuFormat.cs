// C++ 侧 ParticleGpuFormat.h 的镜像。**逐字段**对应，尺寸常量在两端各写一份，
// 由跨语言对拍（Tests~/ParticleGpuDumpTest → Runtime/CSharp/Test）在运行时核：
// 只靠注释说"两边要一致"是守不住的。
//
// 不依赖 Unity API、不需要 NuGet：整目录可以拷进 Unity 的 Assets。

namespace ZEngine.GPUParticle
{
    public enum ParticleGpuCurveMode : uint
    {
        Constant = 0,
        Curve = 1,
        RandomBetweenTwoConstants = 2,
        RandomBetweenTwoCurves = 3,
        Invalid = 4,
    }

    public enum ParticleGpuEmitMode : uint
    {
        ConstantRate = 0,
        Burst = 1,
        Invalid = 2,
    }

    public struct ParticleGpuVec3
    {
        public float X, Y, Z;
    }

    public struct ParticleGpuEmitterDesc
    {
        public float[] Origin;        // 3
        public float Spread;
        public float[] Velocity;      // 3
        public float VelocitySpread;
        public float[] Acceleration;  // 3
        public float Lifetime;
        public float StartSize;
        public float Rate;
        public float[] Reserved;      // 2 —— 补齐到 96 B
        public uint Capacity;
        public uint Mode;
        public uint BurstCount;
        public uint Seed;
        public uint SizeCurve;
        public uint ColorGradient;
        public uint Flags;
        public uint ReservedU;

        public const int Size = 96;   // 16 x f32 + 8 x u32
    }

    // 出生记录：4 x vec4。既能当实例属性（GLES3.0 / WebGL2 没有 SSBO），也能当
    // SSBO / VTF 的一行，两种取址共用同一份字节。
    public struct ParticleGpuSpawn
    {
        public float[] P0;           // 3
        public float SpawnTime;
        public float[] V0;           // 3
        public float Life;
        public float[] A;            // 3
        public float Seed;           // 载体是 float：实例属性通道没有 uint，故掩码到 24 位
        public float Size0;
        public float SizeCurve;
        public float ColorGradient;
        public float Flags;

        public const int Size = 64;  // 4 x vec4
    }

    public struct ParticleGpuCurveRange
    {
        public uint Mode;
        public uint KeyOffset;
        public uint KeyCount;
        public float ConstantMin;
        public float ConstantMax;

        public const int Size = 20;
    }

    public struct ParticleGpuCurveKey
    {
        public float Time;
        public float Value;
    }

    public struct ParticleGpuGradientRange
    {
        public uint ColorKeyOffset;
        public uint ColorKeyCount;
        public uint AlphaKeyOffset;
        public uint AlphaKeyCount;
    }

    public struct ParticleGpuGradientColorKey
    {
        public float Time;
        public float R, G, B;
    }

    public struct ParticleGpuGradientAlphaKey
    {
        public float Time;
        public float A;
    }

    public struct ParticleGpuDrawCmd
    {
        public uint First;
        public uint Count;
        public uint Capacity;
        public uint Flags;
    }

    // 存活窗口：适配器按它发 CmdDrawInstanced(count)。
    // 窗口在环形缓冲里可能绕回 —— 要么把缓冲写两遍，要么在 VS 里取模。
    public struct ParticleGpuWindow
    {
        public uint First;
        public uint Count;
        public uint Capacity;
    }

    public static class ParticleGpuFormatInfo
    {
        public const uint Magic = 0x5A504750u;   // 'ZPGP'
        public const uint Version = 1u;
        public const uint DumpMagic = 0x5A504744u; // 'ZPGD'，跨语言对拍夹具
    }
}
