namespace Scribe.Core.Cleanup;

// OllamaSharp 5.4.30's pull reader awaits a line without the request token. Bind the body reads too, without buffering.
internal sealed class OllamaDownloadReadCancellation(HttpMessageHandler inner, Func<CancellationToken> currentToken)
    : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = currentToken();
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.Content = new BoundContent(response.Content, token);
        return response;
    }

    private sealed class BoundContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly CancellationToken _token;

        public BoundContent(HttpContent inner, CancellationToken token)
        {
            _inner = inner;
            _token = token;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(_token);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new BoundStream(await _inner.ReadAsStreamAsync(_token).ConfigureAwait(false), _token);

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            _inner.CopyToAsync(stream, _token);

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength is not null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class BoundStream(Stream inner, CancellationToken token) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            token.ThrowIfCancellationRequested();
            return inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!cancellationToken.CanBeCanceled || cancellationToken == token)
            {
                return inner.ReadAsync(buffer, token);
            }

            return ReadWithBothAsync(buffer, cancellationToken);
        }

        private async ValueTask<int> ReadWithBothAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            return await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
