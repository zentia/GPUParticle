#pragma once

// =====================================================================
// 容器的**写出**侧（烘焙器用）
// ---------------------------------------------------------------------
// 为什么 writer 要放在 core 里而不是各引擎自己写一份：
// 设计约束是"ZEngine 导入器与 Unity 导入器必须产出逐字节相同的容器"。把写出
// 逻辑放在共用 core 里，这条约束从"靠人自觉"变成"只有一份代码"——
// 与 ParticleGpuFormat.h 的 static_assert 是同一个思路：布局错一个字节不会报错，
// 只会在 GPU 上读到别人的字段，所以要在结构上让它错不了。
//
// 只吃标准库，不碰 RHI、不碰任何引擎。
// =====================================================================

#include "ParticleGpuFormat.h"

#include <cstdint>
#include <vector>

// 烘焙的产物（作者数据，不含任何运行时状态）
struct ParticleGpuContainer
{
    std::vector<ParticleGpuEmitterDesc> emitters;
    std::vector<ParticleGpuCurveRange> curveRanges;
    std::vector<ParticleGpuCurveKey> curveKeys;
    std::vector<ParticleGpuGradientRange> gradientRanges;
    std::vector<ParticleGpuGradientColorKey> gradientColorKeys;
    std::vector<ParticleGpuGradientAlphaKey> gradientAlphaKeys;
};

// 校验：所有曲线 / 渐变下标都在范围内，键数合法。
// 烘焙期花几微秒换掉"运行时在 GPU 上读到别人的字段"。
bool ParticleGpuValidateContainer(const ParticleGpuContainer& c, const char** errorOut = nullptr);

// 拼成字节流（小端，见 ParticleGpuFormat.h 的约定）。
bool ParticleGpuWriteContainer(const ParticleGpuContainer& c, std::vector<uint8_t>& out);

// ---------------------------------------------------------------------
// 等距重采样
// ---------------------------------------------------------------------
// 把**任意**标量曲线烘成 keyCount 个等距键，这样 GPU 侧一次 texelFetch + lerp
// 就够，不用在着色器里做关键帧查找（Unity 的 min-max 双曲线、任意切线模式都
// 在这一步被抹平）。
//
// 关键：sample(t) 由**调用方**给，且必须是引擎自己的曲线求值——
//   Unity 侧：MinMaxCurve / AnimationCurve 的 Evaluate
//   ZEngine 侧：自己的曲线求值
// 于是"曲线语义对齐"这件事从"复刻 + 对拍"降级成"调同一个函数"，这正是把烘焙
// 器写成引擎内 C++ 而不是 C# 的全部理由（见 doc/GPU_PARTICLE_DESIGN.md §六）。
template<class Sample>
void ParticleGpuResampleCurve(std::vector<ParticleGpuCurveKey>& outKeys, int keyCount, Sample&& sample)
{
    outKeys.clear();
    if (keyCount < 2)
        keyCount = 2;
    outKeys.reserve(static_cast<size_t>(keyCount));
    for (int i = 0; i < keyCount; ++i)
    {
        const float t = static_cast<float>(i) / static_cast<float>(keyCount - 1);
        ParticleGpuCurveKey key;
        key.time = t;
        key.value = sample(t);
        outKeys.push_back(key);
    }
}
