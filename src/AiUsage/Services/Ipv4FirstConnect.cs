using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace AiUsage.Services;

/// <summary>
/// The connect step for the app's own HTTP handlers. .NET tries a host's addresses one after another,
/// IPv6 first, and waits out the full system connect timeout on each one. On a network that hands out
/// IPv6 addresses without a working IPv6 route that means a request stalls for 20 seconds or more and
/// then fails, while a browser (which races both families) works fine. This tries the IPv4 addresses
/// first and gives every address only a short attempt before moving on to the next one.
/// </summary>
internal static class Ipv4FirstConnect
{
    /// <summary>How long one address may take to accept the connection before the next one is tried.</summary>
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endPoint = context.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(endPoint.Host, ct).ConfigureAwait(false);
        Exception? last = null;
        foreach (var address in Order(addresses))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(AttemptTimeout);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endPoint.Port), attempt.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException($"Could not connect to {endPoint.Host}.", last);
    }

    /// <summary>IPv4 addresses first, then IPv6, each family in the order DNS returned it.</summary>
    internal static IEnumerable<IPAddress> Order(IEnumerable<IPAddress> addresses) =>
        addresses.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1);
}
