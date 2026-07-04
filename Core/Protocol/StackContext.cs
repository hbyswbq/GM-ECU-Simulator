using Common.Protocol;
using Core.Scheduler;

namespace Core.Protocol;

// Runtime services a stack's Dispatch needs that aren't part of the ECU's persistent state: the
// DPID scheduler (for $AA periodic streams), the DiagnosticStack tag classifying the request CAN
// id, and the resolved StackBinding the request matched. The stack tag is informational only -
// dispatch ownership is structural (per-binding CAN ids + allow-lists), so no handler branches on
// it; the Ford capture dispatch reads it purely to annotate its log lines. Binding carries the
// resolved binding so a dispatch can read its own enabled allow-list: the Ford capture stack is
// CatchAll (it must SEE every SID to log it), so its per-service checklist can't gate at Resolve
// time the way the GM stacks do - it consults Binding.Enabled and NRC-$11s an unticked UDS service.
public sealed record StackContext(DpidScheduler Scheduler, DiagnosticStack Stack, StackBinding? Binding = null);
