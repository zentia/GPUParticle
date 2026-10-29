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

# ---- 跨语言对拍的 C++ 侧（夹具生成器，不直接断言；由下面那条 ctest 驱动）----
gpuparticle_add_test(ParticleGpuDumpTest ParticleGpuDumpTest.cpp)

# ---- 跨语言对拍（C++ 落盘 → C# 用自己的实现重算 → 逐字段比对）----
# 没装 dotnet 就跳过（加码，不是前置条件）。
find_program(GPUPARTICLE_DOTNET NAMES dotnet)
option(GPUPARTICLE_BUILD_CSHARP_PARITY_TEST "C++/C# 跨语言对拍" ON)
if(GPUPARTICLE_BUILD_CSHARP_PARITY_TEST AND GPUPARTICLE_DOTNET)
    add_test(NAME ParticleGpuCSharpParityTest
             COMMAND ${CMAKE_COMMAND}
                     -DDUMP_EXE=$<TARGET_FILE:ParticleGpuDumpTest>
                     -DDUMP_DIR=${CMAKE_BINARY_DIR}/gpuparticle_dump
                     -DCSPROJ=${CMAKE_CURRENT_SOURCE_DIR}/../Runtime/CSharp/Test/ZEngine.GPUParticle.Test.csproj
                     -DDOTNET=${GPUPARTICLE_DOTNET}
                     -P ${CMAKE_CURRENT_SOURCE_DIR}/../Runtime/CSharp/Test/RunParityTest.cmake)
else()
    message(STATUS "GPUParticle: 未提供 dotnet ⇒ 不注册跨语言对拍")
endif()
