using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Specrig;

static class Grpc
{
    // As Go's codes.String() spells them, so that a refusal reads the same in either record.
    static readonly string[] codes =
    [
        "OK", "Canceled", "Unknown", "InvalidArgument", "DeadlineExceeded", "NotFound", "AlreadyExists", "PermissionDenied",
        "ResourceExhausted", "FailedPrecondition", "Aborted", "OutOfRange", "Unimplemented", "Internal", "Unavailable", "DataLoss",
        "Unauthenticated",
    ];

    // The body is copied as the server reads it, not read ahead of it: a stream would wait on a reader that never comes.
    public static async Task Record(Trace trace, HttpContext context, RequestDelegate next)
    {
        var call = trace.Begin();
        var request = new Copying(context.Request.Body);
        context.Request.Body = request;
        context.Features.Set<IRequestBodyPipeFeature>(new RequestBodyPipeFeature(context));
        string? refusal = "Internal";
        try
        {
            await next(context);
            refusal = Refusal(context);
        }
        finally
        {
            trace.End(call, "in", "grpc", context.Request.Path, First(context, request.Copied), refusal);
        }
    }

    static string? Refusal(HttpContext context)
    {
        var trailers = context.Features.Get<IHttpResponseTrailersFeature>()?.Trailers;
        string? Read(string name) => trailers?[name].FirstOrDefault() ?? context.Response.Headers[name].FirstOrDefault();
        if (!int.TryParse(Read("grpc-status"), out var code) || code == 0)
        {
            return null;
        }
        var name = code < codes.Length ? codes[code] : code.ToString();
        var details = Read("grpc-status-details-bin");
        return details is null || Reason(Convert.FromBase64String(Padded(details))) is not { } reason ? name : $"{name} {reason.ToLowerInvariant()}";
    }

    static JsonNode? First(HttpContext context, byte[] body)
    {
        if (body.Length < 5 || body[0] != 0 || BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(1, 4)) is var length && body.Length < 5 + length)
        {
            return null;
        }
        try
        {
            var metadata = context.GetEndpoint()?.Metadata.FirstOrDefault(m => m.GetType().Name == "GrpcMethodMetadata");
            var method = metadata?.GetType().GetProperty("Method")?.GetValue(metadata);
            var request = method?.GetType() is { IsGenericType: true } described ? described.GetGenericArguments()[0] : null;
            var parser = request?.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var message = parser?.GetType().GetMethod("ParseFrom", [typeof(byte[])])?.Invoke(parser, [body[5..(5 + length)]]);
            return message is null ? null : Backend.Json(message.ToString() ?? "");
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    // google.rpc.Status, read by hand: details (3) of Any, type_url (1) and value (2); ErrorInfo's reason (1).
    static string? Reason(ReadOnlySpan<byte> status)
    {
        foreach (var (field, any) in Fields(status))
        {
            if (field != 3)
            {
                continue;
            }
            string? type = null;
            byte[]? value = null;
            foreach (var (part, bytes) in Fields(any))
            {
                if (part == 1)
                {
                    type = Encoding.UTF8.GetString(bytes);
                }
                else if (part == 2)
                {
                    value = bytes;
                }
            }
            if (type?.EndsWith("google.rpc.ErrorInfo") == true && value is not null)
            {
                return Fields(value).Where(f => f.Field == 1).Select(f => Encoding.UTF8.GetString(f.Bytes)).FirstOrDefault();
            }
        }
        return null;
    }

    static List<(int Field, byte[] Bytes)> Fields(ReadOnlySpan<byte> message)
    {
        var fields = new List<(int, byte[])>();
        var at = 0;
        while (at < message.Length)
        {
            var key = Varint(message, ref at);
            switch (key & 7)
            {
                case 0:
                    Varint(message, ref at);
                    break;
                case 1:
                    at += 8;
                    break;
                case 2:
                    var length = (int)Varint(message, ref at);
                    fields.Add(((int)(key >> 3), message.Slice(at, length).ToArray()));
                    at += length;
                    break;
                case 5:
                    at += 4;
                    break;
                default:
                    return fields;
            }
        }
        return fields;
    }

    static ulong Varint(ReadOnlySpan<byte> message, ref int at)
    {
        ulong value = 0;
        for (var shift = 0; at < message.Length; shift += 7)
        {
            var b = message[at++];
            value |= (ulong)(b & 0x7f) << shift;
            if (b < 0x80)
            {
                break;
            }
        }
        return value;
    }

    static string Padded(string base64) => base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');

    sealed class Copying(Stream inner) : Stream
    {
        readonly MemoryStream copy = new();

        public byte[] Copied => copy.ToArray();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            copy.Write(buffer, offset, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            copy.Write(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
