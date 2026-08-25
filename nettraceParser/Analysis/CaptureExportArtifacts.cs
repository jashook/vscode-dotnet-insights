////////////////////////////////////////////////////////////////////////////////
// Module: CaptureExportArtifacts.cs
//
// Notes:
// The three whole-capture aggregates the JSON export computes on its way past,
// handed back so CaptureAnalysisBuilder can read them instead of computing
// them a second time.
//
// WHY THIS DIRECTION. Each of these is a full pass over the CPU samples -
// 16.24M of them on a real 3.23GB capture, where ThreadActivityProfiler alone
// measures 341-591ms - so a --json run that built them once for the export and
// again for the insights would put roughly a second back onto a phase this
// project has spent a lot of effort taking time OUT of.
//
// The obvious alternative was to hoist all three up into Program.cs and pass
// them DOWN into the exporters. That was rejected on risk, not on taste: the
// exporters' output has to stay byte-identical (it is diffed against a
// pre-change build as the standing verification step for this area), and
// moving computation across three call layers is a far better way to perturb
// it than adding an out parameter that changes no ordering and no arithmetic.
// Handing the values outward touches signatures only.
//
// A --insights run never calls the export at all, so on that path Program.cs
// builds these three directly. Either way each is computed exactly once.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Analysis {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Overview;
using DotnetInsights.NetTrace.Threading;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class CaptureExportArtifacts
{
    // From Overview/TimeBreakdownBuilder.cs.
    public TimeBreakdown TimeBreakdown;

    // From Cpu/CpuCategoryBuilder.cs. Null when the capture had no CPU
    // samples, which is the same condition under which the export writes a
    // zeroed category block - a caller must treat null as "no samples", never
    // as "no categories".
    public CpuCategoryBuilder.CategoryTotals[] CategoryTotals;

    // From Threading/ThreadActivityProfiler.cs. Null when the capture carried
    // no thread-pool data, since the threading writer returns before building
    // it in that case.
    public ThreadActivityProfileSet ThreadProfiles;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Analysis)
