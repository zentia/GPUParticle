// C++ 侧 ParticleGpuBatch.h/.cpp 的镜像。
//
// EmulateSpawn 是顶点着色器（Tier-1）的 CPU 版：pos = p0 + v0*t + 0.5*a*t²。
// 注意 0.5f*a*t*t 的结合顺序 —— float 下换个顺序就会差 1 ulp，跨语言对拍会红，
// 所以这里连写法都要跟 C++ / GLSL 保持一致。

namespace ZEngine.GPUParticle
{
    public struct ParticleGpuEmulated
    {
        public float[] Position;   // 3
        public float Age;          // 归一化年龄 t / life
        public float Life;
        public bool Alive;
    }

    public static class ParticleGpuBatch
    {
        public static ParticleGpuEmulated EmulateSpawn(ParticleGpuSpawn spawn, float time)
        {
            ParticleGpuEmulated o = new ParticleGpuEmulated
            {
                Position = new float[3],
                Age = 0.0f,
                Life = spawn.Life,
                Alive = false,
            };
            for (int i = 0; i < 3; ++i)
            {
                o.Position[i] = spawn.P0[i];
            }

            float t = time - spawn.SpawnTime;
            if (t < 0.0f || t > spawn.Life)
            {
                return o;   // 未出生 / 已死
            }

            o.Age = (spawn.Life > 0.0f) ? (t / spawn.Life) : 0.0f;
            o.Position[0] = spawn.P0[0] + spawn.V0[0] * t + 0.5f * spawn.A[0] * t * t;
            o.Position[1] = spawn.P0[1] + spawn.V0[1] * t + 0.5f * spawn.A[1] * t * t;
            o.Position[2] = spawn.P0[2] + spawn.V0[2] * t + 0.5f * spawn.A[2] * t * t;
            o.Alive = true;
            return o;
        }
    }
}
