namespace MainClient.UiTask
{
    public enum AdTrafficTaskStateKind
    {
        Request = 0,
        Start = 1,
        DSP = 2,
        Clickthrough =3,
        Success = 4,
        Complete = 5,
        Error = 6,
        Failure = 7,
        X5Sec = 8,
    }
    public static class AdTrafficTaskExtensions
    {
        public static string FullName(this AdTrafficTaskStateKind type) => type switch
        {
            AdTrafficTaskStateKind.Request => "request",
            AdTrafficTaskStateKind.Start => "start",
            AdTrafficTaskStateKind.DSP => "dsp",
            AdTrafficTaskStateKind.Clickthrough => "click",
            AdTrafficTaskStateKind.Success => "success",
            AdTrafficTaskStateKind.Complete => "complete",
            AdTrafficTaskStateKind.Error => "error",
            AdTrafficTaskStateKind.Failure => "failure",
            AdTrafficTaskStateKind.X5Sec => "x5sec",
            _ => "unknown"
        };
    }
    public class AdTrafficTaskChangedEventArgs : EventArgs
    {
        public AdTrafficTaskStateKind Kind { get; set; }
        public int Id { get; }
        public int Count { get; }
        public string? Data { get; set; }
        public AdTrafficTaskChangedEventArgs(AdTrafficTaskStateKind kind,int id, int count,string? data = null)
        {
            Kind = kind;
            Id = id;
            Count = count;
            Data = data;
        }
    }
}
