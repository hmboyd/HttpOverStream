﻿using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HttpOverStream.Logging;

namespace HttpOverStream.Client
{
    public class DialMessageHandler : HttpMessageHandler
    {
        public const string UnderlyingStreamProperty = "DIAL_UNDERLYING_STREAM";
        private readonly IDial _dial;
        private readonly Version _httpVersion;
        private readonly ILoggerHttpOverStream _logger;

        public DialMessageHandler(IDial dial) : this(dial, null, null)
        {
        }

        public DialMessageHandler(IDial dial, ILoggerHttpOverStream logger) : this(dial, logger, null)
        {
        }

        public DialMessageHandler(IDial dial, Version httpVersion) : this(dial, null, httpVersion)
        {
        }

        public DialMessageHandler(IDial dial, ILoggerHttpOverStream logger, Version httpVersion)
        {
            _dial = dial ?? throw new ArgumentNullException(nameof(dial));
            _logger = logger ?? new NoopLogger();
            _httpVersion = httpVersion ?? HttpVersion.Version10;
        }

        private class DialResponseContent : HttpContent
        {
            private Stream _stream;
            private long? _length;

            public void SetContent(Stream unread, long? length)
            {
                _stream = unread;
                _length = length;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                return _stream.CopyToAsync(stream);
            }

            protected override bool TryComputeLength(out long length)
            {
                if (_length.HasValue)
                {
                    length = _length.Value;
                    return true;
                }
                length = 0;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing)
                {
                    _stream.Dispose();
                }
            }

            protected override Task<Stream> CreateContentReadStreamAsync()
            {
                return Task.FromResult(_stream);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ValidateAndNormalizeRequest(request);
            Stream stream = null;
            try
            {
                _logger.LogVerbose("HttpOS Client: Trying to connect..");
                stream = await _dial.DialAsync(request, cancellationToken).ConfigureAwait(false);

                _logger.LogVerbose("HttpOS Client: Connected.");
                request.Properties.Add(UnderlyingStreamProperty, stream);

                _logger.LogVerbose("HttpOS Client: Writing request");
                await stream.WriteClientMethodAndHeadersAsync(request, cancellationToken).ConfigureAwait(false);

                // as soon as headers are sent, we should begin reading the response, and send the request body concurrently
                // This is because if the server 404s nothing will ever read the response and it'll hang waiting
                // for someone to read it
                var writeContentTask = Task.Run(async () => // Cancel this task if server response detected
                {
                    if (request.Content != null)
                    {
                        _logger.LogVerbose("HttpOS Client: Writing request request.Content.CopyToAsync");
                        await request.Content.CopyToAsync(stream).ConfigureAwait(false);
                    }

                    _logger.LogVerbose("HttpOS Client: stream.FlushAsync");
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogVerbose("HttpOS Client: Finished writing request");
                }, cancellationToken);

                var responseContent = new DialResponseContent();
                var response = new HttpResponseMessage {RequestMessage = request, Content = responseContent};

                _logger.LogVerbose("HttpOS Client: Waiting for response");
                string statusLine = await stream.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogVerbose("HttpOS Client: Read 1st response line");
                ParseStatusLine(response, statusLine);
                _logger.LogVerbose("HttpOS Client: ParseStatusLine");
                for (;;)
                {
                    var line = await stream.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line.Length == 0)
                    {
                        _logger.LogVerbose("HttpOS Client: Found empty line, end of response headers");
                        break;
                    }

                    try
                    {
                        _logger.LogVerbose("HttpOS Client: Parsing line:" + line);
                        (var name, var value) = HttpParser.ParseHeaderNameValues(line);
                        if (!response.Headers.TryAddWithoutValidation(name, value))
                        {
                            response.Content.Headers.TryAddWithoutValidation(name, value);
                        }
                    }
                    catch (FormatException ex)
                    {
                        throw new HttpRequestException("Error parsing header", ex);
                    }
                }

                _logger.LogVerbose("HttpOS Client: Finished reading response header lines");
                responseContent.SetContent(
                    new BodyStream(stream, response.Content.Headers.ContentLength, closeOnReachEnd: true),
                    response.Content.Headers.ContentLength);
                return response;
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("HttpOS Client: connection timed out.");
                stream?.Dispose();
                throw;
            }
            catch(Exception e)
            {
                _logger.LogError("HttpOS Client: Exception:" + e.Message);
                stream?.Dispose();
                throw;
            }
        }

        private void ParseStatusLine(HttpResponseMessage response, string line)
        {
            const int MinStatusLineLength = 12; // "HTTP/1.x 123" 
            if (line.Length < MinStatusLineLength || line[8] != ' ')
            {
                throw new HttpRequestException("Invalid response, expecting HTTP/1.0 or 1.1, was:" + line);
            }

            if (!line.StartsWith("HTTP/1."))
            {                
                throw new HttpRequestException("Invalid response, expecting HTTP/1.0 or 1.1, was:" + line);
            }
            response.Version = _httpVersion;
            // Set the status code
            if (int.TryParse(line.Substring(9,3), out int statusCode))
            {
                response.StatusCode = (HttpStatusCode)statusCode;
            }
            else
            {
                throw new HttpRequestException("Invalid response, can't parse status code. Line was:" + line);
            }
            // Parse (optional) reason phrase
            if (line.Length == MinStatusLineLength)
            {
                response.ReasonPhrase = string.Empty;
            }
            else if (line[MinStatusLineLength] == ' ')
            {
                response.ReasonPhrase = line.Substring(MinStatusLineLength + 1);
            }
            else
            {
                throw new HttpRequestException("Invalid response");
            }
        }

        private void ValidateAndNormalizeRequest(HttpRequestMessage request)
        {
            request.Version = HttpVersion.Version10;
            // Add headers to define content transfer, if not present
            if (request.Headers.TransferEncodingChunked.GetValueOrDefault())
            {
                throw new HttpRequestException("DialMessageHandler does not support chunked encoding");
            }

            // HTTP 1.0 does not support Expect: 100-continue; just disable it.
            if (request.Headers.ExpectContinue == true)
            {
                request.Headers.ExpectContinue = false;
            }
        }
    }
}
