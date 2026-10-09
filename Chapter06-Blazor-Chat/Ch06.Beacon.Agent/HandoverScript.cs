using Beacon.AI;

namespace Ch06.Beacon.Agent;

/// <summary>
/// Chapter 6's addition to the scripted model: a long Markdown handover note, so there is time to
/// press Stop and enough Markdown to exercise the renderer. It quotes a customer's portal message
/// verbatim, raw HTML and a <c>javascript:</c> link included, the way a real model can be led to echo
/// hostile text from a tool result.
/// </summary>
public static class HandoverScript
{
    public const string Note =
        """
        Here's a handover note for **WO-1001** you can pass to the next shift.

        ## Harbor View Dental, EQ-501 (Contoso Air CA-36X)

        The unit is short-cycling and blowing warm air. Priya Raman (TECH-1) is booked from 9:00 to 11:00 today, and the last fault logged was E5, a compressor overload trip, on November 14.

        1. Test the run capacitor first. A weak capacitor is the usual cause and by far the cheapest fix.
        2. If it reads more than 6% low, replace it and watch the compressor through a few full cycles.
        3. Only if the capacitor tests fine, measure compressor amperage before anyone quotes a compressor.

        | Part | SKU | Price | Approval |
        |---|---|---:|---|
        | Dual run capacitor 45/5 uF | CAP-45-5 | $38.50 | Not needed |
        | Scroll compressor 3-ton | CMP-CA36 | $1,795.00 | Dispatcher |

        Parts are under warranty until May 2029, but labor is not, so the visit is billable.

        The customer left this message in the portal, quoted as received:

        > Please call the front desk before arriving <img src="x" onerror="alert('portal')"> or [confirm the visit here](javascript:alert('portal')).

        The capacitor test procedure is in the [CA-36X service manual](https://contoso.example/manuals/ca-36x).
        """;

    public static ScriptRule Rule { get; } = new("handover-note", Reply: ctx =>
        ctx.Mentions("handover") ? ScriptReply.Say(Note) : null);
}
