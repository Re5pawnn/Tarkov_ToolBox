using System.IO;
using System.Net;
using System.Net.Http;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// Shared back-pressure and retry policy for tarkov.dev. Market and tracker
/// refreshes can start together, but they must not create an eight-request burst.
/// </summary>
internal static class TarkovDevRequestPolicy
{
    private const int MaxAttempts = 4;
    private static readonly SemaphoreSlim ConcurrencyGate = new(2, 2);

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        string operation,
        CancellationToken cancellationToken,
        HttpCompletionOption responseCompletion = HttpCompletionOption.ResponseContentRead)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage? response = null;
            try
            {
                await ConcurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    using var request = requestFactory();
                    response = await client.SendAsync(request, responseCompletion, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ConcurrencyGate.Release();
                }

                if (!IsTransientStatus(response.StatusCode) || attempt == MaxAttempts)
                    return response;

                var delay = GetDelay(attempt, response);
                RuntimeLogService.Warning(
                    "网络",
                    "数据服务暂时不可用，准备重试",
                    $"请求: {operation}\n状态: HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n" +
                    $"次数: {attempt} / {MaxAttempts}\n等待: {delay.TotalMilliseconds:N0} ms");
                response.Dispose();
                response = null;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransientException(exception, cancellationToken) && attempt < MaxAttempts)
            {
                response?.Dispose();
                lastFailure = exception;
                var delay = GetDelay(attempt, null);
                RuntimeLogService.Warning(
                    "网络",
                    "数据请求中断，准备重试",
                    $"请求: {operation}\n原因: {exception.Message}\n次数: {attempt} / {MaxAttempts}\n等待: {delay.TotalMilliseconds:N0} ms");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new HttpRequestException($"{operation} 多次重试后仍然失败。", lastFailure);
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        statusCode == (HttpStatusCode)425 ||
        (int)statusCode >= 500;

    private static bool IsTransientException(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException or IOException;

    private static TimeSpan GetDelay(int attempt, HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(Math.Min(delta.TotalMilliseconds, 15_000));
        if (retryAfter?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero) return TimeSpan.FromMilliseconds(Math.Min(until.TotalMilliseconds, 15_000));
        }

        var exponential = 650 * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(8_000, exponential + Random.Shared.Next(80, 360)));
    }
}
