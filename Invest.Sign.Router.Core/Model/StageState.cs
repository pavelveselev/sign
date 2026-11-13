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

    /// <summary>Value stored in <c>SGN_STAGE_INSTANCE.state</c>.</summary>
    public static string ToCode(this StageState state) => state switch
    {
        StageState.Pending => "PENDING",
        StageState.Requested => "REQUESTED",
        StageState.Done => "DONE",
        StageState.Rejected => "REJECTED",
        StageState.Expired => "EXPIRED",
        StageState.Failed => "FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static StageState ParseStageState(string code) => code switch
    {
        "PENDING" => StageState.Pending,
        "REQUESTED" => StageState.Requested,
        "DONE" => StageState.Done,
        "REJECTED" => StageState.Rejected,
        "EXPIRED" => StageState.Expired,
        "FAILED" => StageState.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown stage state."),
    };
}
