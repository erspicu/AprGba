// LLVMSharp.Interop's global LLVM context is not thread-safe; parallel
// xunit collections that all touch LLVM (SpecCompiler, ConditionEvaluator,
// CpuStateLayout, JitSpike) crash with 0xC0000005 in PrintToString /
// CreateMCJITCompiler. Disable test-collection parallelism for the whole
// assembly. Tests within a single class still execute sequentially in
// xunit, so the only loss is per-class concurrency.
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
