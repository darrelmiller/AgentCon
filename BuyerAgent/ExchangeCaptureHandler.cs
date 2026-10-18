using System.Diagnostics;
using System.Net;
using System.Text;

namespace BuyerAgent;

public sealed class ExchangeCaptureHandler(ExchangeStore store) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var operation = request.RequestUri is null ? null : ExchangeStore.RestOperation(request.Method, request.RequestUri);
        if (!store.Enabled || request.RequestUri is null || request.Method != HttpMethod.Post && operation is null)
            return await base.SendAsync(request, cancellationToken);

        var key = store.Begin(request.RequestUri, operation);
        var started = Stopwatch.GetTimestamp();
        void Complete(string state, Exception? error = null) => store.Status(key, state,
            errorType: error?.GetType().Name, durationMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (request.Content is { } content)
            request.Content = new CapturedContent(content, false,
                (body, truncated) => store.Body(key, false, body, truncated, false),
                _ => { }, error => Complete("request failed", error));
        else
            // HTTP+JSON task reads and actions carry no body; record the request line so the call stays visible.
            store.Body(key, false, $"{request.Method} {request.RequestUri.PathAndQuery}", false, false);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            store.Status(key, "receiving", (int)response.StatusCode);
            if (response.Content is { } responseContent)
            {
                var sse = responseContent.Headers.ContentType?.MediaType == "text/event-stream";
                response.Content = new CapturedContent(responseContent, true,
                    (body, truncated) => store.Body(key, true, body, truncated, sse),
                    complete => Complete(complete ? "complete" : "response not fully consumed"),
                    error => Complete(error is OperationCanceledException ? "canceled" : "response failed", error));
            }
            else Complete("complete");
            return response;
        }
        catch (Exception exception)
        {
            Complete(exception is OperationCanceledException ? "canceled" : "transport failed", exception);
            throw;
        }
    }

    private sealed class CapturedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly bool _response;
        private readonly Capture _capture;

        public CapturedContent(HttpContent inner, bool response, Action<string, bool> body,
            Action<bool> done, Action<Exception> failed)
        {
            _inner = inner;
            _response = response;
            _capture = new Capture(body, done, failed);
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            try
            {
                using var tee = new CaptureStream(stream, _capture, leaveOpen: true);
                await _inner.CopyToAsync(tee, cancellationToken);
                _capture.Finish(true);
            }
            catch (Exception exception) { _capture.Fail(exception); throw; }
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            try
            {
                return new CaptureStream(await _inner.ReadAsStreamAsync(cancellationToken), _capture, leaveOpen: false);
            }
            catch (Exception exception) { _capture.Fail(exception); throw; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_response) _capture.Finish(false);
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class Capture(Action<string, bool> body, Action<bool> done, Action<Exception> failed)
    {
        private readonly MemoryStream _bytes = new();
        private bool _truncated;
        private bool _finished;

        public void Append(ReadOnlySpan<byte> bytes)
        {
            if (_finished || (_truncated && _bytes.Length == ExchangeStore.MaxBodyBytes)) return;
            var count = Math.Min(bytes.Length, ExchangeStore.MaxBodyBytes - (int)_bytes.Length);
            _bytes.Write(bytes[..count]);
            _truncated |= count != bytes.Length;
            body(Encoding.UTF8.GetString(_bytes.GetBuffer(), 0, (int)_bytes.Length), _truncated);
        }

        public void Finish(bool complete)
        {
            if (_finished) return;
            _finished = true;
            done(complete);
        }

        public void Fail(Exception exception)
        {
            if (_finished) return;
            _finished = true;
            failed(exception);
        }
    }

    private sealed class CaptureStream(Stream inner, Capture capture, bool leaveOpen) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            try
            {
                var read = inner.Read(buffer);
                if (read == 0 && buffer.Length > 0) capture.Finish(true);
                else capture.Append(buffer[..read]);
                return read;
            }
            catch (Exception exception) { capture.Fail(exception); throw; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var read = await inner.ReadAsync(buffer, cancellationToken);
                if (read == 0 && buffer.Length > 0) capture.Finish(true);
                else capture.Append(buffer.Span[..read]);
                return read;
            }
            catch (Exception exception) { capture.Fail(exception); throw; }
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            capture.Append(buffer);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            capture.Append(buffer.Span);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                capture.Finish(false);
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
