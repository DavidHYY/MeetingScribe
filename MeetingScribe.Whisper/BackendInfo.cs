namespace MeetingScribe.Whisper;

/// <summary>
/// Proof of which native whisper.cpp backend actually loaded and ran, captured at
/// <see cref="WhisperTranscriber"/> initialization time. Exists specifically to catch a
/// silent CPU fallback: a Vulkan package can be referenced and still not be the library
/// that ends up loaded (missing driver, unsupported GPU, load failure), and whisper.cpp's
/// CPU path will simply run instead without throwing.
/// </summary>
/// <param name="LoadedLibrary">
/// The native runtime Whisper.net actually loaded (e.g. "Vulkan", "Cpu", "Cuda", "Metal"). This
/// is the primary signal: the loader tries the configured <c>RuntimeLibraryOrder</c> in
/// sequence, and this reports which entry actually succeeded. "Metal" is synthesized by
/// <see cref="WhisperTranscriber.CreateAsync"/>, not a real <c>RuntimeLibrary</c> enum value -
/// Whisper.net has no Metal case (Metal is compiled directly into the macOS/iOS "Cpu" native
/// library), so without this override a Mac genuinely running on the GPU via Metal would
/// otherwise be reported as "Cpu"/non-GPU.
/// </param>
/// <param name="IsGpuBackend">
/// True when <see cref="LoadedLibrary"/> is a GPU backend (Vulkan/Cuda/Cuda12/CoreML/OpenVino,
/// or the synthesized "Metal" case above once its native device-init log line confirms Metal
/// actually initialized at runtime).
/// </param>
/// <param name="RuntimeInfo">
/// Raw string from whisper.cpp's <c>whisper_print_system_info</c>-style report: compiled feature
/// flags (AVX, AVX2, NEON, VULKAN, CUDA, ...). Confirms the loaded binary was actually built
/// with Vulkan support, distinct from whether it chose to use it at runtime.
/// </param>
/// <param name="NativeLogLines">
/// Native log lines captured during model load and the first transcription call. On a Vulkan
/// build these include the GPU device enumeration and selection lines emitted by ggml-vulkan
/// (e.g. "ggml_vulkan: Found 1 Vulkan devices" / "ggml_vulkan: 0 = Intel(R) UHD Graphics ..."),
/// which is the strongest evidence available that Vulkan is doing real work and not silently
/// falling back to CPU.
/// </param>
public sealed record BackendInfo(
    string LoadedLibrary,
    bool IsGpuBackend,
    string RuntimeInfo,
    IReadOnlyList<string> NativeLogLines);
