// Allocation sampling for the soak runs (package K11): counts the runtime's GCAllocationTick events (one per ~100 KB allocated) by type, heap kind and thread id, so the
// large-object-heap churn behind a --mem-log line can be named. Diagnostics only: it runs when --mem-log is given.
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;

namespace Tsuki.App.Play;

internal sealed class AllocationSampler : EventListener
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _bytes = [];

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);        // GC keyword: GCAllocationTick
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null || e.PayloadNames is null)
        {
            return;
        }

        string type = "?";
        long amount = 0;
        uint kind = 0;
        for (int i = 0; i < e.PayloadNames.Count; i++)
        {
            switch (e.PayloadNames[i])
            {
                case "TypeName":
                    type = e.Payload[i] as string ?? "?";
                    break;
                case "AllocationAmount64":
                    amount = Convert.ToInt64(e.Payload[i], CultureInfo.InvariantCulture);
                    break;
                case "AllocationKind":
                    kind = Convert.ToUInt32(e.Payload[i], CultureInfo.InvariantCulture);
                    break;
            }
        }

        string key = $"{(kind == 0 ? "small" : kind == 1 ? "LOH" : "POH")} {type} @{e.OSThreadId}";
        lock (_gate)
        {
            _bytes[key] = _bytes.GetValueOrDefault(key) + amount;
        }
    }

    /// <summary>The biggest allocators since the last call (MiB), and clears the counts.</summary>
    public string Take(int top)
    {
        KeyValuePair<string, long>[] list;
        lock (_gate)
        {
            list = [.. _bytes];
            _bytes.Clear();
        }

        var sb = new StringBuilder("  allocation ticks (MiB since the last line):");
        foreach (KeyValuePair<string, long> kv in list.OrderByDescending(k => k.Value).Take(top))
        {
            sb.Append(CultureInfo.InvariantCulture, $" [{kv.Key} {kv.Value >> 20}]");
        }

        return sb.ToString();
    }
}
