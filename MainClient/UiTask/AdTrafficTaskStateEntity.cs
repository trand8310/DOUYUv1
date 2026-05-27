namespace MainClient.UiTask
{
    public sealed class AdTrafficTaskStateEntity
    {
        public long Request;
        public long Start;
        public long DSP;
        public long Clickthrough;
        public long Success;
        public long Error;
        public long Failure;
        public long Complete;


        private long _deltaStart;
        private long _deltaDsp;
        private long _deltaClickthrough;

        public double ClickRatio => DSP == 0 ? 0 : (double)Clickthrough / DSP;

        public void Add(AdTrafficTaskStateKind type, int count)
        {
            switch (type)
            {
                case AdTrafficTaskStateKind.Request:
                    Interlocked.Add(ref Request, count);
                    break;

                case AdTrafficTaskStateKind.Start:
                    Interlocked.Add(ref Start, count);
                    Interlocked.Add(ref _deltaStart, count);
                    break;

                case AdTrafficTaskStateKind.DSP:
                    Interlocked.Add(ref DSP, count);
                    Interlocked.Add(ref _deltaDsp, count);
                    break;

                case AdTrafficTaskStateKind.Clickthrough:
                    Interlocked.Add(ref Clickthrough, count);
                    Interlocked.Add(ref _deltaClickthrough, count);
                    break;

                case AdTrafficTaskStateKind.Success:
                    Interlocked.Add(ref Success, count);
                    break;

                case AdTrafficTaskStateKind.Error:
                    Interlocked.Add(ref Error, count);
                    break;

                case AdTrafficTaskStateKind.Failure:
                    Interlocked.Add(ref Failure, count);
                    break;

                case AdTrafficTaskStateKind.Complete:
                    Interlocked.Add(ref Complete, count);
                    break;
            }
        }

        public AdTrafficTaskStateSnapshot GetSnapshot()
        {
            return new AdTrafficTaskStateSnapshot(
                Start: Interlocked.Read(ref _deltaStart),
                Dsp: Interlocked.Read(ref _deltaDsp),
                Click: Interlocked.Read(ref _deltaClickthrough)
            );
        }

        public void Commit(AdTrafficTaskStateSnapshot delta)
        {
            if (delta.Start != 0) Interlocked.Add(ref _deltaStart, -delta.Start);
            if (delta.Dsp != 0) Interlocked.Add(ref _deltaDsp, -delta.Dsp);
            if (delta.Click != 0) Interlocked.Add(ref _deltaClickthrough, -delta.Click);
        }

        public Dictionary<string, long> ToMetricDictionary(AdTrafficTaskStateSnapshot delta)
        {
            var dict = new Dictionary<string, long>(3);
            if (delta.Start > 0) dict["start"] = delta.Start;
            if (delta.Dsp > 0) dict["dsp"] = delta.Dsp;
            if (delta.Click > 0) dict["click"] = delta.Click;
            return dict;
        }
    }
}
