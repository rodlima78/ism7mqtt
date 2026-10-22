using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ism7mqtt.ISM7.Protocol;
using Xunit;

namespace ism7mqtt.Tests;

public class WriteTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task Coalescer_BurstForOneKey_SendsOnlyTheLastValue()
    {
        var coalescer = new WriteCoalescer(Quiet);
        var sent = new ConcurrentQueue<string>();
        var tasks = new List<Task>();
        // Thirty presses of a "−" button, 3.0 down to 0.1, 20 ms apart: one write, with the last value.
        for (int i = 30; i >= 1; i--)
        {
            var value = (i / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            tasks.Add(coalescer.ScheduleAsync("CHA/Temperaturueberhoehung_Sammler", _ => { sent.Enqueue(value); return Task.CompletedTask; }, CancellationToken.None));
            await Task.Delay(20);
        }
        await Task.WhenAll(tasks);
        Assert.Equal(new[] { "0.1" }, sent.ToArray());
    }

    [Fact]
    public async Task Coalescer_DifferentKeys_AreSentEach()
    {
        var coalescer = new WriteCoalescer(Quiet);
        var sent = new ConcurrentQueue<string>();
        await Task.WhenAll(
            coalescer.ScheduleAsync("CHA/A", _ => { sent.Enqueue("A"); return Task.CompletedTask; }, CancellationToken.None),
            coalescer.ScheduleAsync("CHA/B", _ => { sent.Enqueue("B"); return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(new[] { "A", "B" }, new SortedSet<string>(sent));
    }

    [Fact]
    public async Task Coalescer_AfterTheQuietPeriod_ANewCommandIsSentAgain()
    {
        var coalescer = new WriteCoalescer(Quiet);
        var sent = new ConcurrentQueue<string>();
        await coalescer.ScheduleAsync("CHA/A", _ => { sent.Enqueue("1"); return Task.CompletedTask; }, CancellationToken.None);
        await coalescer.ScheduleAsync("CHA/A", _ => { sent.Enqueue("2"); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(new[] { "1", "2" }, sent.ToArray());
    }

    [Fact]
    public async Task Coalescer_Cancelled_SendsNothing()
    {
        var coalescer = new WriteCoalescer(Quiet);
        var sent = 0;
        using var cts = new CancellationTokenSource();
        var task = coalescer.ScheduleAsync("CHA/A", _ => { Interlocked.Increment(ref sent); return Task.CompletedTask; }, cts.Token);
        cts.Cancel();
        await task;
        Assert.Equal(0, sent);
    }

    [Fact]
    public void Refusal_AcceptedBundle_IsNull()
    {
        var resp = new TelegramBundleResp
        {
            BundleId = "7",
            State = TelegrResponseState.OK,
            WriteTelegrams = new List<InfonumberWriteResp> { new() { State = TelegrResponseState.OK, InfoNumber = 10, BusAddress = "0x8" } },
        };
        Assert.Null(Ism7Client.DescribeWriteRefusal(resp));
    }

    [Fact]
    public void Refusal_NamesTheBundleStateAndEachRefusedTelegram()
    {
        var resp = new TelegramBundleResp
        {
            BundleId = "12",
            State = TelegrResponseState.ER,
            Errormsg = "busy",
            WriteTelegrams = new List<InfonumberWriteResp>
            {
                new() { State = TelegrResponseState.OK, InfoNumber = 10, BusAddress = "0x8" },
                new() { State = TelegrResponseState.ER, InfoNumber = 11, BusAddress = "0x8" },
            },
        };
        var text = Ism7Client.DescribeWriteRefusal(resp);
        Assert.StartsWith("write 12 refused:", text);
        Assert.Contains("busy", text);
        Assert.Contains("info number 11", text);
        Assert.DoesNotContain("info number 10", text);
    }

    [Fact]
    public void Refusal_WithoutTelegrams_StillDescribed()
    {
        var resp = new TelegramBundleResp { BundleId = "3", State = TelegrResponseState.ER };
        Assert.Contains("write 3 refused", Ism7Client.DescribeWriteRefusal(resp));
    }
}
