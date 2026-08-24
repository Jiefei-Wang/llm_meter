using LLMMeter.Core;

namespace LLMMeter.Adapters;

/// <summary>
/// Tracks llama.cpp /slots across polls to derive per-slot generation rates.
/// Rates are only computed while the same task continues on a slot
/// (id_task unchanged); task changes re-baseline instead of inventing numbers.
/// </summary>
public sealed class SlotTracker(double emaAlpha = 0.35)
{
    private sealed class SlotState
    {
        public long TaskId;
        public long LastDecoded;
        public long LastTicks;
        public double Ema;
        public bool HasEma;
        public bool Seen;
    }

    private readonly Dictionary<int, SlotState> _states = new();
    private readonly double _alpha = Math.Clamp(emaAlpha, 0.05, 1.0);

    public RequestSnapshot? Observe(int slotId, long taskId, long promptTokens, long decoded, long nowTicks,
        bool processing, long prefilledTokens = -1, long cachedTokens = -1)
    {
        if (!processing)
            return null; // idle/completed slots stop reporting

        if (!_states.TryGetValue(slotId, out var st))
        {
            st = new SlotState();
            _states[slotId] = st;
        }

        MetricValue<double> rate = MetricValue<double>.None;

        if (decoded >= 0)
        {
            if (!st.Seen || st.TaskId != taskId)
            {
                // New task on this slot: baseline without inventing a rate.
                st.TaskId = taskId;
                st.LastDecoded = decoded;
                st.LastTicks = nowTicks;
                st.HasEma = false;
            }
            else
            {
                double dt = (double)(nowTicks - st.LastTicks) / System.Diagnostics.Stopwatch.Frequency;
                if (dt > 0.0005 && decoded >= st.LastDecoded)
                {
                    double r = (decoded - st.LastDecoded) / dt;
                    st.Ema = st.HasEma ? _alpha * r + (1 - _alpha) * st.Ema : r;
                    st.HasEma = true;
                    rate = MetricValue<double>.Approx(st.Ema, MetricSource.Derived, $"/slots slot {slotId} n_decoded delta");
                }
                st.LastDecoded = decoded;
                st.LastTicks = nowTicks;
            }
        }

        st.Seen = true;

        return new RequestSnapshot
        {
            Id = taskId >= 0 ? $"#{taskId}" : $"#{slotId}",
            InputTokens = promptTokens >= 0
                ? MetricValue<long>.Exact(promptTokens, MetricSource.NativeApi, "/slots")
                : MetricValue<long>.None,
            CachedTokens = cachedTokens >= 0
                ? MetricValue<long>.Exact(cachedTokens, MetricSource.NativeApi, "/slots")
                : MetricValue<long>.None,
            PrefilledTokens = prefilledTokens >= 0
                ? MetricValue<long>.Exact(prefilledTokens, MetricSource.NativeApi, "/slots")
                : MetricValue<long>.None,
            OutputTokens = decoded >= 0
                ? MetricValue<long>.Exact(decoded, MetricSource.NativeApi, "/slots")
                : MetricValue<long>.None,
            TokensPerSecond = rate,
        };
    }

    public void Reset() => _states.Clear();
}
