using LLMMeter.Core;

namespace LLMMeter.Adapters;

/// <summary>
/// Tracks llama.cpp /slots across polls to derive per-slot prefill and generation rates.
/// Rates are only computed while the same task continues on a slot
/// (id_task unchanged); task changes re-baseline instead of inventing numbers.
/// </summary>
public sealed class SlotTracker
{
    private sealed class SlotState
    {
        public long TaskId;
        public long LastDecoded;
        public long LastPrefilled;
        public long LastDecodedTicks;
        public long LastPrefilledTicks;
        public bool Seen;
    }

    private readonly Dictionary<int, SlotState> _states = new();

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

        MetricValue<double> decodeRate = MetricValue<double>.None;
        MetricValue<double> prefillRate = MetricValue<double>.None;

        if (!st.Seen || st.TaskId != taskId)
        {
            // New task on this slot: baseline without inventing a rate.
            st.TaskId = taskId;
            st.LastDecoded = decoded;
            st.LastPrefilled = prefilledTokens;
            st.LastDecodedTicks = nowTicks;
            st.LastPrefilledTicks = nowTicks;
        }
        else
        {
            if (decoded >= 0 && st.LastDecoded >= 0)
            {
                if (decoded > st.LastDecoded)
                {
                    double dt = (double)(nowTicks - st.LastDecodedTicks) / System.Diagnostics.Stopwatch.Frequency;
                    double r = (decoded - st.LastDecoded) / dt;
                    if (dt > 0.0005)
                        decodeRate = MetricValue<double>.Approx(r, MetricSource.Derived, $"/slots slot {slotId} n_decoded delta");
                    st.LastDecoded = decoded;
                    st.LastDecodedTicks = nowTicks;
                }
                else if (decoded == st.LastDecoded)
                {
                    decodeRate = MetricValue<double>.Approx(0, MetricSource.Derived, $"/slots slot {slotId} n_decoded unchanged");
                }
                else
                {
                    st.LastDecoded = decoded;
                    st.LastDecodedTicks = nowTicks;
                }
            }
            if (prefilledTokens >= 0 && st.LastPrefilled >= 0)
            {
                if (prefilledTokens > st.LastPrefilled)
                {
                    double dt = (double)(nowTicks - st.LastPrefilledTicks) / System.Diagnostics.Stopwatch.Frequency;
                    double r = (prefilledTokens - st.LastPrefilled) / dt;
                    if (dt > 0.0005)
                        prefillRate = MetricValue<double>.Approx(r, MetricSource.Derived, $"/slots slot {slotId} n_prompt_tokens_processed delta");
                    st.LastPrefilled = prefilledTokens;
                    st.LastPrefilledTicks = nowTicks;
                }
                else if (prefilledTokens == st.LastPrefilled)
                {
                    prefillRate = MetricValue<double>.Approx(0, MetricSource.Derived, $"/slots slot {slotId} n_prompt_tokens_processed unchanged");
                }
                else
                {
                    st.LastPrefilled = prefilledTokens;
                    st.LastPrefilledTicks = nowTicks;
                }
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
            PrefillTokensPerSecond = prefillRate,
            TokensPerSecond = decodeRate,
        };
    }

    public void Reset() => _states.Clear();
}
