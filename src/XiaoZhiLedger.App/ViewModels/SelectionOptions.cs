namespace XiaoZhiLedger.App.ViewModels;

public sealed record SelectionOption(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record CardScaleOption(double Value, string Label)
{
    public override string ToString() => Label;
}
