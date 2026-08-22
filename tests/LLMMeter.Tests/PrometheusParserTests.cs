using LLMMeter.Collection;
using Xunit;

namespace LLMMeter.Tests;

public class PrometheusParserTests
{
    private const string VllmSample = """
        # HELP vllm:num_requests_running Number of requests in model execution batches.
        # TYPE vllm:num_requests_running gauge
        vllm:num_requests_running{model_name="meta-llama/Llama-3.1-8B-Instruct"} 8.0
        # HELP vllm:num_requests_waiting Number of requests waiting to be processed.
        # TYPE vllm:num_requests_waiting gauge
        vllm:num_requests_waiting{model_name="meta-llama/Llama-3.1-8B-Instruct"} 3.0
        # HELP vllm:prompt_tokens_total Number of prefill tokens processed.
        # TYPE vllm:prompt_tokens_total counter
        vllm:prompt_tokens_total{model_name="meta-llama/Llama-3.1-8B-Instruct"} 1245678.0
        # HELP vllm:generation_tokens_total Number of generation tokens processed.
        # TYPE vllm:generation_tokens_total counter
        vllm:generation_tokens_total{model_name="meta-llama/Llama-3.1-8B-Instruct"} 982344.0
        # HELP vllm:kv_cache_usage_perc Fraction of KV cache blocks in use
        # TYPE vllm:kv_cache_usage_perc gauge
        vllm:kv_cache_usage_perc{model_name="meta-llama/Llama-3.1-8B-Instruct"} 0.71
        # HELP vllm:time_to_first_token_seconds Histogram of time to first token
        # TYPE vllm:time_to_first_token_seconds histogram
        vllm:time_to_first_token_seconds_bucket{le="0.001",model_name="m1"} 0.0
        vllm:time_to_first_token_seconds_bucket{le="+Inf",model_name="m1"} 140.0
        vllm:time_to_first_token_seconds_sum{model_name="m1"} 42.5
        vllm:time_to_first_token_seconds_count{model_name="m1"} 140.0
        """;

    private const string LlamaCppSample = """
        # HELP llamacpp:prompt_tokens_total Number of prompt tokens processed.
        # TYPE llamacpp:prompt_tokens_total counter
        llamacpp:prompt_tokens_total 30
        # HELP llamacpp:tokens_predicted_total Number of generation tokens processed.
        # TYPE llamacpp:tokens_predicted_total counter
        llamacpp:tokens_predicted_total 10
        # HELP llamacpp:requests_processing Number of requests processing.
        # TYPE llamacpp:requests_processing gauge
        llamacpp:requests_processing 2
        # HELP llamacpp:requests_deferred Number of requests deferred.
        # TYPE llamacpp:requests_deferred gauge
        llamacpp:requests_deferred 1
        """;

    [Fact]
    public void Parses_Vllm_Sample()
    {
        var samples = PrometheusParser.Parse(VllmSample);

        Assert.Contains(samples, s => s.Name == "vllm:num_requests_running" && Math.Abs(s.Value - 8.0) < 1e-9);
        Assert.Contains(samples, s => s.Name == "vllm:num_requests_waiting" && Math.Abs(s.Value - 3.0) < 1e-9);
        Assert.Contains(samples, s => s.Name == "vllm:prompt_tokens_total" && Math.Abs(s.Value - 1245678) < 1e-9);
        Assert.Contains(samples, s => s.Name == "vllm:generation_tokens_total");
        Assert.Contains(samples, s => s.Name == "vllm:kv_cache_usage_perc" && Math.Abs(s.Value - 0.71) < 1e-9);

        var ttftCount = samples.Single(s => s.Name == "vllm:time_to_first_token_seconds_count");
        Assert.Equal("140", ttftCount.Value.ToString());
        Assert.True(ttftCount.TryGetLabel("model_name", out var mn));
        Assert.Equal("m1", mn);
    }

    [Fact]
    public void Parses_LlamaCpp_Sample()
    {
        var samples = PrometheusParser.Parse(LlamaCppSample);

        Assert.Equal(4, samples.Count);
        Assert.Contains(samples, s => s.Name == "llamacpp:prompt_tokens_total" && s.Value == 30);
        Assert.Contains(samples, s => s.Name == "llamacpp:tokens_predicted_total" && s.Value == 10);
        Assert.Contains(samples, s => s.Name == "llamacpp:requests_processing" && s.Value == 2);
        Assert.Contains(samples, s => s.Name == "llamacpp:requests_deferred" && s.Value == 1);
    }

    [Theory]
    [InlineData("metric_without_labels 12.5")]
    [InlineData("m{a=\"x\",b=\"y\"} -3")]
    [InlineData("m 1e3")]
    [InlineData("m 123 1700000000000")] // trailing timestamp ignored
    public void Parses_Line_Variants(string line)
    {
        var sample = PrometheusParser.ParseLine(line);
        Assert.NotNull(sample);
    }

    [Fact]
    public void Handles_Escapes_NaN_And_Inf()
    {
        // label value contains: line<NL>break "q" \
        const string line = """msg{err="line\nbreak \"q\" \\"} 1""";
        var s1 = PrometheusParser.ParseLine(line);
        Assert.NotNull(s1);
        Assert.Equal("line\nbreak \"q\" \\", s1!.Labels["err"]);

        Assert.Equal(double.NaN, PrometheusParser.ParseLine("m NaN")!.Value);
        Assert.Equal(double.PositiveInfinity, PrometheusParser.ParseLine("m +Inf")!.Value);
        Assert.Equal(double.NegativeInfinity, PrometheusParser.ParseLine("m -Inf")!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# just a comment")]
    [InlineData("garbage")]
    [InlineData("m{unterminated=\"label} 1")]
    [InlineData("m{=} 1")]
    [InlineData("{no_name} 1")]
    [InlineData("m abc")]
    public void Skips_Malformed_Lines(string line)
    {
        Assert.Null(PrometheusParser.ParseLine(line));
        Assert.Empty(PrometheusParser.Parse(line));
    }

    [Fact]
    public void One_Bad_Line_Does_Not_Fail_The_Scrape()
    {
        var text = "good_metric 5\nbroken_line !!!\nanother_good 6\n";
        var samples = PrometheusParser.Parse(text);
        Assert.Equal(2, samples.Count);
        Assert.Equal(6, samples[1].Value);
    }
}
