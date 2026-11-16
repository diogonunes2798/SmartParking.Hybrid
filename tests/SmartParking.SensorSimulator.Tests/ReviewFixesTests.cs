using System.Net;
using System.Text;
using SmartParking.SensorSimulator;

namespace SmartParking.SensorSimulator.Tests;

/// <summary>Findings of the Phase 7 code review (docs/revisoes/fase-7-revisao-codigo.md) that have a unit test.</summary>
public sealed class ReviewFixesTests
{
    [Fact]
    public void Descending_gen2_ramp_is_rejected()
    {
        // L2: a second-generation sensor never goes back to generation 1 (README, ADR-0011).
        var problems = ScenarioOptions.Parse(["--gen2-initial-ratio", "1", "--gen2-ratio", "0", "--gen2-ramp-start", "10"]).Validate();
        Assert.Contains(problems, p => p.Contains("never goes back", StringComparison.Ordinal));
    }

    [Fact]
    public void Null_peak_windows_are_reported_not_thrown()
    {
        // L1: "peakWindows": null used to throw a NullReferenceException in Validate.
        var path = Path.Combine(Path.GetTempPath(), $"scenario-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "name": "x", "peakFactor": 10, "peakWindows": null }""");
        try
        {
            var options = ScenarioOptions.Load(path);
            Assert.Contains(options.Validate(), p => p.Contains("peakWindows", StringComparison.Ordinal));
            Assert.Single(options.EffectivePeakWindows); // falls back to the default window, no exception
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("--scenario")]                              // L3: no file
    [InlineData("--seed", "99999999999999999999")]          // L1: beyond long
    [InlineData("--seed", "abc")]
    public void Bad_arguments_are_argument_errors(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ScenarioOptions.Parse(args));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("../x")]
    public void Scenario_name_must_be_safe_for_names_and_folders(string name)
    {
        Assert.NotEmpty((new ScenarioOptions { Name = name }).Validate());
    }

    [Fact]
    public async Task Provision_false_only_looks_up_and_stops_when_something_is_missing()
    {
        // M3: --provision false used to be ignored (the provisioner always created what was missing).
        using var handler = new EmptyApi();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test") };
        var options = new ScenarioOptions { Name = "nothing-here", Facilities = 1, Spots = 2, Provision = false };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Provisioner(http, options, TextWriter.Null).ProvisionAsync(TestContext.Current.CancellationToken));

        Assert.Contains("--provision", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Posts); // nothing created
    }

    [Fact]
    public void Summary_csv_has_one_header_and_the_rnf04_counts()
    {
        // L7: the counts of the RNF-04 pilot (errors, requests without an answer) in a versioned CSV, spec §6 format.
        var path = Path.Combine(Path.GetTempPath(), $"summary-{Guid.NewGuid():N}.csv");
        var options = new ScenarioOptions { Name = "pilot", Run = 3 };
        var summary = new RunCounters().ToSummary(options, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), 0, (0, 0, 0), 0, cancelled: false);
        try
        {
            SummaryCsvWriter.Append(path, options, summary);
            SummaryCsvWriter.Append(path, options, summary);
            var lines = File.ReadAllLines(path);

            Assert.Equal(SummaryCsvWriter.Header, lines[0]);
            Assert.Single(lines, l => l == SummaryCsvWriter.Header);
            Assert.Contains(lines, l => l.StartsWith("pilot,hybrid,sim_errors,0,events,3,", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("pilot,hybrid,sim_no_response,0,events,3,", StringComparison.Ordinal));
            Assert.All(lines.Skip(1), l => Assert.Equal(7, l.Split(',').Length));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>An API with no facilities, spots or sensors; counts POSTs.</summary>
    private sealed class EmptyApi : HttpMessageHandler
    {
        public int Posts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
        }
    }
}
