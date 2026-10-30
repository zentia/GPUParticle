# =====================================================================
# GPUParticle 测试
# ---------------------------------------------------------------------
# 全部不依赖任何引擎（ZEngine / Unity）——核心只吃标准库，也不依赖 rapidjson
# （GPUSpine 的测试要 json 夹具，粒子这边暂时没有外部夹具格式）。
#
#   cmake -S .. -B build -DGPUPARTICLE_BUILD_TESTS=ON
#   cmake --build build --config Debug
#   ctest --test-dir build -C Debug
# =====================================================================

if(MSVC)
    # 测试里用 fopen / sprintf 落盘与打日志，MSVC 会报 C4996。
    add_compile_definitions(_CRT_SECURE_NO_WARNINGS)
endif()

function(gpuparticle_add_test name)
    add_executable(${name} ${ARGN})
    target_link_libraries(${name} PRIVATE GPUParticle::Core)
    set_target_properties(${name} PROPERTIES CXX_STANDARD 20 FOLDER "GPUParticle/Tests")
    if(MSVC)
        target_compile_options(${name} PRIVATE /W4 /utf-8)
    endif()
endfunction()

# ---- 核心自测：曲线 / 渐变 / 随机 / 发射调度 / 闭式积分镜像 ----
gpuparticle_add_test(ParticleGpuCoreTest ParticleGpuCoreTest.cpp)
add_test(NAME ParticleGpuCoreTest COMMAND ParticleGpuCoreTest)

# ---------------------------------------------------------------------
# 曾经这里有"跨语言对拍"（ParticleGpuDumpTest 落盘 → C# 镜像重算 → 逐字段比），
# 2026-10 已整目录删除，原因记录在 doc/GPU_PARTICLE_DESIGN.md §六：
# Unity 侧运行时改成把本库直接编进引擎（引擎内 C++ 烘焙器），C# 镜像不在任何
# ship 路径上，只剩"第三份拷贝"的维护税。
# ---------------------------------------------------------------------
