using Beacon.Client.Core;
using Ch15.Forms;

namespace Ch15.Beacon.Desk.Components.Questions;

/// <summary>The bridge from Beacon.Client.Core's view of an interrupt to Ch15.Forms' platform-independent question.</summary>
public static class InterruptQuestions
{
    public static FormQuestion ToQuestion(this InterruptView interrupt) =>
        new(interrupt.Id, interrupt.Message, interrupt.ResponseSchema, interrupt.ExpiresAt, interrupt.Metadata);
}
