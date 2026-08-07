using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace MCPForUnity.Editor.Services.AssetGen.Http
{
    /// <summary>
    /// Production <see cref="IHttpTransport"/> backed by UnityWebRequest. Must be invoked on the
    /// Unity main thread (the asset-gen job manager guarantees this in Phase 3). The send is
    /// awaited via a <see cref="TaskCompletionSource{T}"/> wired to the async op's completed
    /// callback, so the call never blocks the editor loop.
    /// </summary>
    public sealed class UnityWebRequestTransport : IHttpTransport
    {
        internal const long MaximumSupportedResponseBytes = 512L * 1024L * 1024L;

        private sealed class BoundedDownloadHandler : DownloadHandlerScript
        {
            private readonly MemoryStream _buffer = new MemoryStream();
            private readonly long _maximumBytes;

            internal bool ExceededLimit { get; private set; }

            internal BoundedDownloadHandler(long maximumBytes)
                : base(new byte[64 * 1024])
            {
                _maximumBytes = maximumBytes;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0) return true;
                if (_buffer.Length + dataLength > _maximumBytes)
                {
                    ExceededLimit = true;
                    return false;
                }

                _buffer.Write(data, 0, dataLength);
                return true;
            }

            internal byte[] GetBytes() => ExceededLimit ? null : _buffer.ToArray();
            internal void DisposeBuffer() => _buffer.Dispose();
        }

        public Task<HttpResult> SendAsync(HttpRequestSpec spec, CancellationToken ct)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));

            long responseLimit = EffectiveResponseLimit(spec);

            var tcs = new TaskCompletionSource<HttpResult>();

            var downloadHandler = new BoundedDownloadHandler(responseLimit);

            var request = new UnityWebRequest(spec.Url, spec.Method ?? UnityWebRequest.kHttpVerbGET)
            {
                downloadHandler = downloadHandler,
                redirectLimit = 0
            };
            if (spec.Body != null)
            {
                request.uploadHandler = new UploadHandlerRaw(spec.Body);
            }
            if (!string.IsNullOrEmpty(spec.ContentType))
            {
                request.SetRequestHeader("Content-Type", spec.ContentType);
            }
            if (spec.Headers != null)
            {
                foreach (var kv in spec.Headers)
                {
                    request.SetRequestHeader(kv.Key, kv.Value);
                }
            }
            CancellationTokenRegistration ctReg = default;
            if (ct.CanBeCanceled)
            {
                ctReg = ct.Register(() =>
                {
                    try { request.Abort(); } catch { /* ignore */ }
                    tcs.TrySetCanceled();
                });
            }

            var op = request.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    byte[] body = downloadHandler.GetBytes();
                    var result = new HttpResult
                    {
                        Status = (int)request.responseCode,
                        Body = body,
                        Text = body == null || !spec.DecodeResponseText
                            ? null
                            : Encoding.UTF8.GetString(body),
                        IsSuccess = !downloadHandler.ExceededLimit
                            && request.result == UnityWebRequest.Result.Success
                    };
                    tcs.TrySetResult(result);
                }
                catch (Exception e)
                {
                    tcs.TrySetException(e);
                }
                finally
                {
                    ctReg.Dispose();
                    request.Dispose();
                    downloadHandler.DisposeBuffer();
                }
            };

            return tcs.Task;
        }

        internal static long EffectiveResponseLimit(HttpRequestSpec spec)
        {
            long requested = spec?.MaxResponseBytes ?? 0;
            if (requested <= 0) return 16L * 1024L * 1024L;
            return Math.Min(requested, MaximumSupportedResponseBytes);
        }

        /// <summary>True iff the request carries an Authorization header (case-insensitive key).</summary>
        internal static bool CarriesAuth(HttpRequestSpec spec)
        {
            if (spec?.Headers == null) return false;
            foreach (var kv in spec.Headers)
                if (string.Equals(kv.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
