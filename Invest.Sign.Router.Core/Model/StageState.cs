namespace Invest.Sign.Router.Core.Model;

/// <summary>State of a single stage instance.</summary>
public enum StageState
{
    /// <summary>Stage entered, signing request not created yet.</summary>
    Pending,

    /// <summary>Signing request created, waiting for the result.</summary>
    Requested,

    Done,
    Rejected,
    Expired,
    Failed,
}

public static class StageStateExtensions
{
    public static bool IsTerminal(this StageState state) =>
        state is not (StageState.Pending or StageState.Requested);
}
