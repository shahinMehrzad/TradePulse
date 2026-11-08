namespace Domain.Enums;

/// <summary>What a finished candle told about an open paper trade. Not stored.</summary>
public enum CandleVerdictEnum : byte
{
    /// <summary>The candle changed nothing: neither level was reached.</summary>
    StillOpen,

    /// <summary>The candle ended the trade.</summary>
    Closed,

    /// <summary>
    /// Both the target and the stop were reached inside the candle and nothing in it says which came first. The trade is left as it is and
    /// the caller asks for the trades of that stretch and replays them in order: the answer is certain, never assumed.
    /// </summary>
    NeedsTrades
}
