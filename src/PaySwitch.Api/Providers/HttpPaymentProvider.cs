using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaySwitch.Api.Providers;

public sealed class HttpPaymentProvider(string name, HttpClient http) : IPaymentProvider
{
    public string Name { get; } = name;

    public Task<ProviderResult> AuthorizeAsync(ProviderCall call, CancellationToken ct) => SendAsync("authorize", call, ct);

    public Task<ProviderResult> CaptureAsync(ProviderCall call, CancellationToken ct) => SendAsync("capture", call, ct);

    public Task<ProviderResult> ReverseAsync(ProviderCall call, CancellationToken ct) => SendAsync("reverse", call, ct);

    private async Task<ProviderResult> SendAsync(string operation, ProviderCall call, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, operation)
        {
            Content = JsonContent.Create(new { reference = call.Reference, amount = call.Amount, currency = call.Currency, token = call.Token }),
        };

        request.Headers.Add("Idempotency-Key", $"{call.Reference}:{operation}");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderTransientException($"{Name} {operation}: {ex.Message}", ex);
        }

        using (response)
        {
            var status = response.StatusCode;
            if (status == HttpStatusCode.OK)
            {
                var body = await ReadAsync(response, ct);
                return new ProviderResult(ProviderOutcome.Approved, body?.Ref);
            }

            if (status == HttpStatusCode.PaymentRequired)
            {
                var body = await ReadAsync(response, ct);
                return new ProviderResult(ProviderOutcome.Declined, DeclineCode: body?.Code ?? "declined");
            }

            if (status == HttpStatusCode.NotFound && operation == "reverse")
            {
                return new ProviderResult(ProviderOutcome.Approved);
            }

            if ((int)status >= 500 || status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.RequestTimeout)
            {
                throw new ProviderTransientException($"{Name} {operation}: HTTP {(int)status}");
            }

            throw new ProviderPermanentException($"{Name} {operation}: unexpected HTTP {(int)status}");
        }
    }

    private sealed record Body(string? Ref, string? Code);

    private static async Task<Body?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<Body>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
