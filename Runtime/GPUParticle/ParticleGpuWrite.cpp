#include "ParticleGpuWrite.h"

#include <cstring>

namespace
{
    template<class T>
    void AppendPod(std::vector<uint8_t>& out, const T& v)
    {
        const uint8_t* bytes = reinterpret_cast<const uint8_t*>(&v);
        out.insert(out.end(), bytes, bytes + sizeof(T));
    }

    // 一个 chunk = 头（id / 单元素字节数 / 元素数）+ 元素数组。
    // elementSize 显式写进文件：跨语言校验用（C# / TS 读的时候先比 sizeof，
    // 对不上就说明某一端的结构改了而另一端没跟）。
    template<class T>
    void AppendChunk(std::vector<uint8_t>& out, ParticleGpuChunkId id, const std::vector<T>& elements)
    {
        if (elements.empty())
            return;   // 空 chunk 不写：读端按"没有这个 chunk"处理

        ParticleGpuChunkHeader header;
        header.id = static_cast<uint32_t>(id);
        header.elementSize = static_cast<uint32_t>(sizeof(T));
        header.elementCount = static_cast<uint32_t>(elements.size());
        header.reserved = 0;
        AppendPod(out, header);

        const uint8_t* bytes = reinterpret_cast<const uint8_t*>(elements.data());
        out.insert(out.end(), bytes, bytes + sizeof(T) * elements.size());
    }
}  // namespace

bool ParticleGpuValidateContainer(const ParticleGpuContainer& c, const char** errorOut)
{
    const char* error = nullptr;

    if (c.emitters.empty())
        error = "no emitters";

    if (error == nullptr)
    {
        for (size_t i = 0; i < c.emitters.size() && error == nullptr; ++i)
        {
            const ParticleGpuEmitterDesc& e = c.emitters[i];
            if (e.sizeCurve >= c.curveRanges.size())
                error = "emitter.sizeCurve out of range";
            else if (e.colorGradient >= c.gradientRanges.size())
                error = "emitter.colorGradient out of range";
            else if (e.capacity == 0)
                error = "emitter.capacity is zero";
            else if (e.lifetime <= 0.0f)
                error = "emitter.lifetime must be positive";
        }
    }

    if (error == nullptr)
    {
        for (size_t i = 0; i < c.curveRanges.size() && error == nullptr; ++i)
        {
            const ParticleGpuCurveRange& r = c.curveRanges[i];
            if (r.mode >= static_cast<uint32_t>(ParticleGpuCurveMode::Invalid))
                error = "curve mode invalid";
            else if (r.keyCount == 0)
                error = "curve has no keys";
            else if (static_cast<size_t>(r.keyOffset) + static_cast<size_t>(r.keyCount) > c.curveKeys.size())
                error = "curve keys out of range";
        }
    }

    if (error == nullptr)
    {
        for (size_t i = 0; i < c.gradientRanges.size() && error == nullptr; ++i)
        {
            const ParticleGpuGradientRange& g = c.gradientRanges[i];
            if (static_cast<size_t>(g.colorKeyOffset) + static_cast<size_t>(g.colorKeyCount) > c.gradientColorKeys.size())
                error = "gradient color keys out of range";
            else if (static_cast<size_t>(g.alphaKeyOffset) + static_cast<size_t>(g.alphaKeyCount) > c.gradientAlphaKeys.size())
                error = "gradient alpha keys out of range";
        }
    }

    if (errorOut != nullptr)
        *errorOut = error;
    return error == nullptr;
}

bool ParticleGpuWriteContainer(const ParticleGpuContainer& c, std::vector<uint8_t>& out)
{
    if (!ParticleGpuValidateContainer(c))
        return false;

    out.clear();

    ParticleGpuHeader header;
    header.magic = kParticleGpuMagic;
    header.version = kParticleGpuVersion;
    header.emitterCount = static_cast<uint32_t>(c.emitters.size());
    header.curveCount = static_cast<uint32_t>(c.curveRanges.size());
    header.gradientCount = static_cast<uint32_t>(c.gradientRanges.size());
    header.flags = 0;
    header.reserved[0] = 0;
    header.reserved[1] = 0;
    AppendPod(out, header);

    // chunk 顺序 = ParticleGpuChunkId 的声明顺序（读端按 id 分派，顺序无关，
    // 但固定顺序让 diff 稳定）。
    AppendChunk(out, ParticleGpuChunkId::EmitterDescs, c.emitters);
    AppendChunk(out, ParticleGpuChunkId::CurveRanges, c.curveRanges);
    AppendChunk(out, ParticleGpuChunkId::CurveKeys, c.curveKeys);
    AppendChunk(out, ParticleGpuChunkId::GradientColorKeys, c.gradientColorKeys);
    AppendChunk(out, ParticleGpuChunkId::GradientAlphaKeys, c.gradientAlphaKeys);
    AppendChunk(out, ParticleGpuChunkId::GradientRanges, c.gradientRanges);

    ParticleGpuChunkHeader end;
    end.id = static_cast<uint32_t>(ParticleGpuChunkId::End);
    end.elementSize = 0;
    end.elementCount = 0;
    end.reserved = 0;
    AppendPod(out, end);

    return true;
}
