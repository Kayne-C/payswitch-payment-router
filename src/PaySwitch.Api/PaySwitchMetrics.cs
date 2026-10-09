using System.Diagnostics.Metrics;

namespace PaySwitch.Api;

public static class PaySwitchMetrics
{
    public static readonly Meter Meter = new("PaySwitch");

    public static readonly Counter<long> ProviderCalls = Meter.CreateCounter<long>("payswitch.provider.calls");
    public static readonly Counter<long> PaymentsFinished = Meter.CreateCounter<long>("payswitch.payments.finished");
    public static readonly Counter<long> Compensations = Meter.CreateCounter<long>("payswitch.compensations");
}
