using System.Net;
using System.Security.Cryptography;
using System.Text;
using KittenIO.Protocol;

namespace KittenIO.Mod.Networking;

/// <summary>
/// Strict, bounded HTTP/1.1 WebSocket upgrade handling for KittenIO's private
/// loopback listener. Reading stops exactly at CRLF CRLF so no WebSocket frame
/// bytes are consumed into an HTTP reader's private buffer.
/// </summary>
internal static class LoopbackWebSocketUpgrade
{
    public const int MaximumHeaderBytes = 16 * 1024;
    public const int MaximumHeaderCount = 64;
    private const int MaximumRequestLineBytes = 2 * 1024;
    private const string WebSocketMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static async ValueTask<HttpUpgradeRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var bytes = new MemoryStream(capacity: MaximumHeaderBytes);
        var oneByte = new byte[1];
        var terminatorState = 0;
        while (bytes.Length < MaximumHeaderBytes)
        {
            var count = await stream.ReadAsync(oneByte.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw HttpUpgradeException.BadRequest("The HTTP upgrade request ended before its headers were complete.");
            }

            var value = oneByte[0];
            if (value >= 0x7f || value < 0x20 && value is not (byte)'\r' and not (byte)'\n' and not (byte)'\t')
            {
                throw HttpUpgradeException.BadRequest("The HTTP upgrade request contained invalid header bytes.");
            }

            bytes.WriteByte(value);
            terminatorState = (terminatorState, value) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                (_, (byte)'\r') => 1,
                _ => 0,
            };
            if (terminatorState == 4) break;
        }

        if (terminatorState != 4)
        {
            throw new HttpUpgradeException(
                HttpStatusCode.RequestHeaderFieldsTooLarge,
                "Request Header Fields Too Large",
                "HTTP upgrade headers exceed KittenIO's 16 KiB limit.\n");
        }

        var text = Encoding.ASCII.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
        var content = text[..^4];
        var withoutProperLines = content.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        if (withoutProperLines.Contains('\r', StringComparison.Ordinal) ||
            withoutProperLines.Contains('\n', StringComparison.Ordinal))
        {
            throw HttpUpgradeException.BadRequest("HTTP headers must use canonical CRLF line endings.");
        }

        var lines = content.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0 || Encoding.ASCII.GetByteCount(lines[0]) > MaximumRequestLineBytes)
        {
            throw HttpUpgradeException.BadRequest("The HTTP request line is missing or too long.");
        }

        var requestParts = lines[0].Split(' ', StringSplitOptions.None);
        if (requestParts.Length != 3 || requestParts.Any(static part => part.Length == 0) ||
            !string.Equals(requestParts[0], "GET", StringComparison.Ordinal) ||
            !string.Equals(requestParts[2], "HTTP/1.1", StringComparison.Ordinal))
        {
            throw HttpUpgradeException.BadRequest("KittenIO requires an HTTP/1.1 GET WebSocket upgrade.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                throw HttpUpgradeException.BadRequest("Empty or folded HTTP headers are forbidden.");
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw HttpUpgradeException.BadRequest("An HTTP header is malformed.");
            }

            var name = line[..colon];
            if (!name.All(IsHeaderTokenCharacter))
            {
                throw HttpUpgradeException.BadRequest("An HTTP header name is malformed.");
            }

            var value = line[(colon + 1)..].Trim(' ', '\t');
            if (!headers.TryAdd(name, value))
            {
                throw HttpUpgradeException.BadRequest("Duplicate HTTP headers are forbidden.");
            }

            if (headers.Count > MaximumHeaderCount)
            {
                throw new HttpUpgradeException(
                    HttpStatusCode.RequestHeaderFieldsTooLarge,
                    "Request Header Fields Too Large",
                    "HTTP upgrade contains too many headers.\n");
            }
        }

        return new HttpUpgradeRequest(requestParts[1], headers);
    }

    public static WebSocketUpgradeDecision Evaluate(
        HttpUpgradeRequest request,
        string expectedPath,
        string pairingToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingToken);

        if (!string.Equals(request.Target, expectedPath, StringComparison.Ordinal))
        {
            return WebSocketUpgradeDecision.Reject(HttpStatusCode.NotFound, "Not Found", "Not found\n");
        }

        // Preserve the established policy order. Any browser Origin is rejected,
        // even when its value is empty or otherwise unusual.
        if (request.Headers.ContainsKey("Origin"))
        {
            return WebSocketUpgradeDecision.Reject(
                HttpStatusCode.Forbidden,
                "Forbidden",
                "Browser origins are forbidden\n");
        }

        if (!request.Headers.TryGetValue("X-KittenIO-Protocol", out var protocol) ||
            !string.Equals(
                protocol,
                ProtocolConstants.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return WebSocketUpgradeDecision.Reject(
                HttpStatusCode.UpgradeRequired,
                "Upgrade Required",
                "Unsupported KittenIO protocol\n");
        }

        request.Headers.TryGetValue("Authorization", out var authorization);
        const string bearerPrefix = "Bearer ";
        var suppliedToken = authorization is not null && authorization.StartsWith(bearerPrefix, StringComparison.Ordinal)
            ? authorization[bearerPrefix.Length..]
            : string.Empty;
        if (suppliedToken.Length == 0 || !FixedTimeTokenEquals(suppliedToken, pairingToken))
        {
            return WebSocketUpgradeDecision.Reject(
                HttpStatusCode.Unauthorized,
                "Unauthorized",
                "Unauthorized\n",
                [new("WWW-Authenticate", "Bearer realm=KittenIO")]);
        }

        if (!request.Headers.ContainsKey("Host") ||
            !request.Headers.TryGetValue("Connection", out var connection) ||
            !ContainsToken(connection, "Upgrade") ||
            !request.Headers.TryGetValue("Upgrade", out var upgrade) ||
            !string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase))
        {
            return WebSocketUpgradeDecision.Reject(
                HttpStatusCode.BadRequest,
                "Bad Request",
                "Invalid WebSocket upgrade\n");
        }

        if (!request.Headers.TryGetValue("Sec-WebSocket-Version", out var webSocketVersion) ||
            !string.Equals(webSocketVersion, "13", StringComparison.Ordinal))
        {
            return WebSocketUpgradeDecision.Reject(
                HttpStatusCode.UpgradeRequired,
                "Upgrade Required",
                "Unsupported WebSocket version\n",
                [new("Sec-WebSocket-Version", "13")]);
        }

        if (!request.Headers.TryGetValue("Sec-WebSocket-Key", out var key) || !IsCanonicalWebSocketKey(key))
        {
            return WebSocketUpgradeDecision.Reject(
                HttpStatusCode.BadRequest,
                "Bad Request",
                "Invalid WebSocket key\n");
        }

        var acceptBytes = SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketMagic));
        return WebSocketUpgradeDecision.Accept(Convert.ToBase64String(acceptBytes));
    }

    public static async ValueTask WriteResponseAsync(
        Stream stream,
        WebSocketUpgradeDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var body = decision.IsAccepted ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(decision.Body);
        var builder = new StringBuilder(256)
            .Append("HTTP/1.1 ")
            .Append(decision.StatusCode)
            .Append(' ')
            .Append(decision.ReasonPhrase)
            .Append("\r\n");

        if (decision.IsAccepted)
        {
            builder.Append("Upgrade: websocket\r\n")
                .Append("Connection: Upgrade\r\n")
                .Append("Sec-WebSocket-Accept: ")
                .Append(decision.AcceptKey)
                .Append("\r\n");
        }
        else
        {
            builder.Append("Content-Type: text/plain; charset=utf-8\r\n")
                .Append("Content-Length: ")
                .Append(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("\r\n")
                .Append("Connection: close\r\n");
        }

        foreach (var header in decision.AdditionalHeaders)
        {
            builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }
        builder.Append("\r\n");

        var headers = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(headers.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool FixedTimeTokenEquals(string supplied, string expected)
    {
        var suppliedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedDigest, expectedDigest);
    }

    private static bool IsCanonicalWebSocketKey(string value)
    {
        try
        {
            var decoded = Convert.FromBase64String(value);
            return decoded.Length == 16 && string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool ContainsToken(string values, string expected) =>
        values.Split(',').Any(value => string.Equals(value.Trim(), expected, StringComparison.OrdinalIgnoreCase));

    private static bool IsHeaderTokenCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}

internal sealed record HttpUpgradeRequest(
    string Target,
    IReadOnlyDictionary<string, string> Headers);

internal readonly record struct HttpHeader(string Name, string Value);

internal sealed record WebSocketUpgradeDecision(
    int StatusCode,
    string ReasonPhrase,
    string Body,
    string? AcceptKey,
    IReadOnlyList<HttpHeader> AdditionalHeaders)
{
    public bool IsAccepted => StatusCode == (int)HttpStatusCode.SwitchingProtocols;

    public static WebSocketUpgradeDecision Accept(string acceptKey) => new(
        (int)HttpStatusCode.SwitchingProtocols,
        "Switching Protocols",
        string.Empty,
        acceptKey,
        []);

    public static WebSocketUpgradeDecision Reject(
        HttpStatusCode statusCode,
        string reasonPhrase,
        string body,
        IReadOnlyList<HttpHeader>? headers = null) => new(
            (int)statusCode,
            reasonPhrase,
            body,
            null,
            headers ?? []);
}

internal sealed class HttpUpgradeException(
    HttpStatusCode statusCode,
    string reasonPhrase,
    string responseBody) : Exception(responseBody.Trim())
{
    public WebSocketUpgradeDecision Decision { get; } = WebSocketUpgradeDecision.Reject(
        statusCode,
        reasonPhrase,
        responseBody);

    public static HttpUpgradeException BadRequest(string message) => new(
        HttpStatusCode.BadRequest,
        "Bad Request",
        $"{message}\n");
}
