using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using Strama.Capture;
using Strama.Records;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

// Aliases to resolve ambiguity: FFmpeg.AutoGen also defines ID3D11Device,
// ID3D11DeviceContext, and ID3D11Texture2D as opaque COM stubs.
using D3D11Device  = Vortice.Direct3D11.ID3D11Device;
using D3D11Context = Vortice.Direct3D11.ID3D11DeviceContext;
using D3D11Tex2D   = Vortice.Direct3D11.ID3D11Texture2D;

namespace Strama.Encode;

// Opaque COM stub types for FFmpeg.AutoGen's AVD3D11VADeviceContext fields.
// FFmpeg.AutoGen 8.x does not always generate these on Windows; we define
// them here as empty structs so the pointer casts compile correctly.
internal unsafe struct ID3D11Device_ { }
internal unsafe struct ID3D11DeviceContext_ { }

/// <summary>
/// Captures the screen and encodes frames to H.264 entirely in-process using
/// FFmpeg.AutoGen, then sends them over RTP/UDP. No ffmpeg subprocess or pipe.
///
/// Encoder selection (HandshakeConfig.Encoder):
///   "auto"      — probe in order: h264_amf → h264_nvenc → h264_qsv → libx264
///   "h264_amf"  — AMD GPU (D3D11 zero-copy path, no CPU involvement)
///   "h264_nvenc"— NVIDIA GPU (D3D11 zero-copy path)
///   "libx264"   — software CPU fallback
/// </summary> 
public sealed unsafe class RtpFrameEncoder : IFrameEncoder
{
    private readonly HandshakeConfig _config;

    static RtpFrameEncoder()
    {
        if (string.IsNullOrEmpty(ffmpeg.RootPath))
            ffmpeg.RootPath = Environment.GetEnvironmentVariable("FFMPEG_ROOT")
                           ?? AppContext.BaseDirectory;
        DynamicallyLoadedBindings.Initialize();
    }

    public RtpFrameEncoder(HandshakeConfig config) => _config = config;

    public void Run(CancellationToken ct = default)
    {
        try { RunCapture(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[Encode] Fatal: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[Encode] Stack:\n{ex.StackTrace}");
            if (ex.InnerException != null)
                Console.WriteLine($"[Encode]        {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
    }

    private void RunCapture(CancellationToken ct)
    {
        string encoder = DetectEncoder(_config.Encoder);
        bool   isGpu   = encoder is "h264_amf" or "h264_nvenc" or "h264_qsv";

        Console.WriteLine($"[Encode] Mode: {(isGpu ? "GPU" : "CPU")} ({encoder})");

        if (isGpu && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            RunEncodeGpu(encoder, ct);
        }
        else
        {
            using var capturer    = ScreenCapturerFactory.Create();
            using var session     = new CaptureSession(capturer);
            var       captureTask = Task.Run(() => session.Run(ct), ct);
            try
            {
                RunEncode(session.Frames, ct);
            }
            finally
            {
                try { captureTask.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    // Returns the encoder name to use.
    // "auto" probes in priority order, checking both that the codec is present in
    // this FFmpeg build AND that a matching GPU adapter exists. Explicit choices
    // are honored if compatible, else fall back to libx264.
    private static string DetectEncoder(string configured)
    {
        if (configured != "auto")
        {
            if (ffmpeg.avcodec_find_encoder_by_name(configured) == null)
            {
                Console.WriteLine($"[Encode] '{configured}' not found in FFmpeg build, falling back to libx264");
                return "libx264";
            }
            if (!AdapterSelector.IsHardwareAvailable(configured))
            {
                Console.WriteLine($"[Encode] '{configured}' has no matching GPU adapter on this system, falling back to libx264");
                return "libx264";
            }
            return configured;
        }

        foreach (var name in new[] { "h264_amf", "h264_nvenc", "h264_qsv", "libx264" })
        {
            if (ffmpeg.avcodec_find_encoder_by_name(name) == null) continue;
            if (!AdapterSelector.IsHardwareAvailable(name))
            {
                Console.WriteLine($"[Encode] '{name}' present in FFmpeg build but no matching adapter; skipping");
                continue;
            }
            Console.WriteLine($"[Encode] auto → {name}");
            return name;
        }
        return "libx264";
    }

    // ─── GPU path ─────────────────────────────────────────────────────────────
    //
    // Runs capture + encode on a single thread so the D3D11 immediate context is
    // never accessed concurrently (immediate context has no built-in thread safety).
    //
    // Data flow — zero CPU involvement after setup:
    //   DXGI acquired texture (GPU)
    //     → CopyResource → intermediate D3D11 texture (our pool, GPU)
    //     → AVFrame wrapping that texture (noop buf[0])
    //     → h264_amf / h264_nvenc encodes entirely on GPU
    //     → RTP packets → UDP
    //
    // Note: the GPU path uses native capture resolution (no resize).
    // OutputWidth / OutputHeight from the config are ignored here.
    private void RunEncodeGpu(string encoderName, CancellationToken ct)
    {
        // Two D3D11 devices to allow cross-adapter scenarios. They reference the
        // same instance when capture and encode happen on the same adapter (the
        // common single-GPU case).
        D3D11Device?            displayDevice = null;
        D3D11Context?           displayCtx    = null;
        D3D11Device?            encoderDevice = null;
        D3D11Context?           encoderCtx    = null;
        IDXGIAdapter1?          displayAdapter = null;
        IDXGIAdapter1?          encoderAdapter = null;
        IDXGIOutputDuplication? duplication    = null;
        SharedTexturePool?      pool           = null;

        AVCodecContext*  codecCtx    = null;
        AVFormatContext* fmtCtx      = null;
        AVPacket*        packet      = null;
        AVBufferRef*     hwDevBuf    = null;
        AVBufferRef*     hwFramesBuf = null;

        // Packets from the priming phase (encoded before the muxer is open).
        // Freed in the finally if an exception prevents them from being written.
        var bufferedPackets = new List<nint>();

        try
        {
            // ── Adapter selection ─────────────────────────────────────────────
            // Capture must run on the adapter that owns the display output
            // (DuplicateOutput won't work otherwise). The encoder must run on
            // the adapter matching its vendor (NVENC on NVIDIA, AMF on AMD, etc).
            // On hybrid laptops these can be different — we bridge with shared
            // NT-handle textures + keyed mutexes.
            displayAdapter = AdapterSelector.GetAdapterOwningPrimaryOutput()
                             ?? throw new InvalidOperationException(
                                 "No DXGI adapter owns a display output.");
            var encoderVendor = AdapterSelector.VendorForEncoder(encoderName);
            encoderAdapter    = encoderVendor is null
                                ? null
                                : AdapterSelector.FindAdapterByVendor(encoderVendor.Value);

            bool crossAdapter = encoderAdapter is not null
                                && !AdapterSelector.SameAdapter(displayAdapter, encoderAdapter);

            // ── D3D11 devices ─────────────────────────────────────────────────
            // BgraSupport lets both devices accept BGRA-format shared resources,
            // which is what DXGI Desktop Duplication produces. Without it the
            // encoder device can refuse to open a shared BGRA texture from the
            // display device with E_INVALIDARG.
            (displayDevice, displayCtx) = D3D11AdapterDevice.Create(
                displayAdapter, DeviceCreationFlags.BgraSupport);
            if (crossAdapter)
                (encoderDevice, encoderCtx) = D3D11AdapterDevice.Create(
                    encoderAdapter, DeviceCreationFlags.BgraSupport);
            else
            {
                encoderDevice = displayDevice;
                encoderCtx    = displayCtx;
            }

            Console.WriteLine(
                $"[Encode] Capture on '{displayAdapter.Description1.Description.Trim()}', " +
                $"encode on '{(encoderAdapter ?? displayAdapter).Description1.Description.Trim()}' " +
                $"({(crossAdapter ? "cross-adapter" : "same-adapter")})");

            // ── DXGI duplication on the display device ────────────────────────
            using (var dxgiDevice = displayDevice.QueryInterface<IDXGIDevice>())
            using (var devAdapter = dxgiDevice.GetParent<IDXGIAdapter>())
            {
                devAdapter.EnumOutputs(0, out var output).CheckError();
                using (output)
                {
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    duplication = output1.DuplicateOutput(displayDevice);
                }
            }

            // Acquire one frame just to read capture dimensions.
            // DXGI doesn't expose the desktop resolution outside of a frame.
            int    srcW          = 0;
            int    srcH          = 0;
            Format captureFormat = Format.Unknown;

            while (srcW == 0 && !ct.IsCancellationRequested)
            {
                var r = duplication.AcquireNextFrame(100, out _, out var res);
                if (r == Vortice.DXGI.ResultCode.WaitTimeout) continue;
                r.CheckError();
                using (res)
                {
                    using var tex = res.QueryInterface<D3D11Tex2D>();
                    srcW          = (int)tex.Description.Width;
                    srcH          = (int)tex.Description.Height;
                    captureFormat = tex.Description.Format;
                }
                duplication.ReleaseFrame().CheckError();
            }
            if (ct.IsCancellationRequested) return;

            Console.WriteLine($"[Encode] {srcW}x{srcH} GPU native, encoder={encoderName}");

            // ── Intermediate texture pool ─────────────────────────────────────
            // Same-adapter: plain pool on the one device.
            // Cross-adapter: paired pool — texture instance on display side and
            // mirror instance on encoder side, sharing underlying VRAM via NT
            // handle, with keyed-mutex synchronization between sides.
            const int PoolSize = 3;
            pool = SharedTexturePool.Create(
                displayDevice, encoderDevice,
                srcW, srcH, captureFormat,
                PoolSize, crossAdapter);

            // ── FFmpeg hardware device context ────────────────────────────────
            // Wraps the ENCODER device so the GPU encoder reads from the right
            // adapter's VRAM. In same-adapter mode the encoder device is the
            // display device, matching the original behavior.
            hwDevBuf = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            if (hwDevBuf == null) throw new InvalidOperationException("av_hwdevice_ctx_alloc failed");

            var hwDevCtx   = (AVHWDeviceContext*)hwDevBuf->data;
            var d3d11HwCtx = (AVD3D11VADeviceContext*)hwDevCtx->hwctx;
            // FFmpeg's d3d11va_device_uninit calls Release() on both pointers.
            // AddRef here so our Vortice wrappers remain valid after FFmpeg frees its context.
            Marshal.AddRef(encoderDevice.NativePointer);
            Marshal.AddRef(encoderCtx.NativePointer);
            d3d11HwCtx->device         = (FFmpeg.AutoGen.ID3D11Device*)encoderDevice.NativePointer.ToPointer();
            d3d11HwCtx->device_context = (FFmpeg.AutoGen.ID3D11DeviceContext*)encoderCtx.NativePointer.ToPointer();

            int ret = ffmpeg.av_hwdevice_ctx_init(hwDevBuf);
            if (ret < 0) ThrowFfmpegError("av_hwdevice_ctx_init", ret);

            // ── FFmpeg hardware frames context ────────────────────────────────
            // Provides format/dimension metadata to the encoder.
            // initial_pool_size = 0: we supply our own textures; FFmpeg allocates none.
            hwFramesBuf = ffmpeg.av_hwframe_ctx_alloc(hwDevBuf);
            if (hwFramesBuf == null) throw new InvalidOperationException("av_hwframe_ctx_alloc failed");

            var hwFramesCtx = (AVHWFramesContext*)hwFramesBuf->data;
            hwFramesCtx->format            = AVPixelFormat.AV_PIX_FMT_D3D11;
            hwFramesCtx->sw_format         = AVPixelFormat.AV_PIX_FMT_BGRA;
            hwFramesCtx->width             = srcW;
            hwFramesCtx->height            = srcH;
            hwFramesCtx->initial_pool_size = 0;

            ret = ffmpeg.av_hwframe_ctx_init(hwFramesBuf);
            if (ret < 0) ThrowFfmpegError("av_hwframe_ctx_init", ret);

            // ── Codec ─────────────────────────────────────────────────────────
            var codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
            if (codec == null) throw new InvalidOperationException($"Encoder '{encoderName}' not found");

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (codecCtx == null) throw new OutOfMemoryException("avcodec_alloc_context3 failed");

            codecCtx->width         = srcW;
            codecCtx->height        = srcH;
            codecCtx->pix_fmt       = AVPixelFormat.AV_PIX_FMT_D3D11;
            codecCtx->time_base     = new AVRational { num = 1, den = _config.Framerate };
            codecCtx->framerate     = new AVRational { num = _config.Framerate, den = 1 };
            codecCtx->bit_rate      = ParseBitrate(_config.Bitrate);
            codecCtx->gop_size      = _config.Framerate / 2;  // IDR every ~0.5 s → inline SPS/PPS
            codecCtx->flags        |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDevBuf);
            codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(hwFramesBuf);

            AVDictionary* encOpts = null;
            AddEncoderOptions(encoderName, &encOpts);
            ret = ffmpeg.avcodec_open2(codecCtx, codec, &encOpts);
            ffmpeg.av_dict_free(&encOpts);
            if (ret < 0) ThrowFfmpegError("avcodec_open2", ret);

            packet = ffmpeg.av_packet_alloc();
            if (packet == null) throw new OutOfMemoryException("av_packet_alloc failed");

            // ── Priming: encode until the first packet arrives ────────────────
            // Hardware encoders (AMF, NVENC) only populate codecCtx->extradata
            // (SPS/PPS) after the first successful encode call. We must have
            // extradata before avformat_write_header so the decoder receives the
            // stream parameters it needs to start decoding.
            long pts    = 0;
            int  texIdx = 0;

            while (bufferedPackets.Count == 0 && !ct.IsCancellationRequested)
            {
                var primeResult = duplication.AcquireNextFrame(100, out _, out var primeRes);
                if (primeResult == Vortice.DXGI.ResultCode.WaitTimeout) continue;
                primeResult.CheckError();

                int primeIdx = texIdx;
                texIdx = (texIdx + 1) % PoolSize;

                pool.AcquireForCapture(primeIdx);
                using (primeRes)
                {
                    using var dxgiTex = primeRes.QueryInterface<D3D11Tex2D>();
                    displayCtx.CopyResource(pool.DisplaySide[primeIdx], dxgiTex);
                }
                duplication.ReleaseFrame().CheckError();
                pool.ReleaseAfterCapture(primeIdx);

                pool.AcquireForEncode(primeIdx);
                try
                {
                    var primeFrame = ffmpeg.av_frame_alloc();
                    if (primeFrame == null) continue;

                    primeFrame->format        = (int)AVPixelFormat.AV_PIX_FMT_D3D11;
                    primeFrame->hw_frames_ctx = ffmpeg.av_buffer_ref(hwFramesBuf);
                    primeFrame->data[0]       = (byte*)pool.EncoderSide[primeIdx].NativePointer.ToPointer();
                    primeFrame->data[1]       = (byte*)(nuint)0;
                    primeFrame->width         = srcW;
                    primeFrame->height        = srcH;
                    primeFrame->pts           = pts++;
                    primeFrame->buf[0]        = ffmpeg.av_buffer_alloc(1);

                    if (ffmpeg.avcodec_send_frame(codecCtx, primeFrame) >= 0)
                    {
                        while (ffmpeg.avcodec_receive_packet(codecCtx, packet) == 0)
                        {
                            var clone = ffmpeg.av_packet_clone(packet);
                            if (clone != null) bufferedPackets.Add((nint)clone);
                            ffmpeg.av_packet_unref(packet);
                        }
                    }
                    ffmpeg.av_frame_free(&primeFrame);
                }
                finally
                {
                    pool.ReleaseAfterEncode(primeIdx);
                }
            }
            if (ct.IsCancellationRequested) return;

            // Diagnose: show what the encoder produced during priming
            {
                Console.Write($"[Encode] Primed: extradata={codecCtx->extradata_size}B");
                if (codecCtx->extradata_size > 0)
                {
                    Console.Write(" extradata[0..4]=");
                    for (int i = 0; i < Math.Min(4, codecCtx->extradata_size); i++)
                        Console.Write($"{codecCtx->extradata[i]:X2} ");
                }
                Console.WriteLine($"  buffered={bufferedPackets.Count} pkt(s)");
                if (bufferedPackets.Count > 0)
                {
                    var p0 = (AVPacket*)bufferedPackets[0];
                    Console.Write($"[Encode] First pkt: size={p0->size} flags={p0->flags:X} data[0..8]=");
                    for (int i = 0; i < Math.Min(8, p0->size); i++)
                        Console.Write($"{p0->data[i]:X2} ");
                    Console.WriteLine();
                }
            }

            // ── RTP output (opened after extradata is populated) ──────────────
            string url = $"rtp://{_config.UdpIP}:{_config.UdpPort}";

            AVFormatContext* fmtCtxTmp = null;
            ret = ffmpeg.avformat_alloc_output_context2(&fmtCtxTmp, null, "rtp", url);
            if (ret < 0) ThrowFfmpegError("avformat_alloc_output_context2", ret);
            fmtCtx = fmtCtxTmp;

            var rtpStream = ffmpeg.avformat_new_stream(fmtCtx, null);
            if (rtpStream == null) throw new InvalidOperationException("avformat_new_stream failed");

            ret = ffmpeg.avcodec_parameters_from_context(rtpStream->codecpar, codecCtx);
            if (ret < 0) ThrowFfmpegError("avcodec_parameters_from_context", ret);
            rtpStream->time_base = codecCtx->time_base;

            AVDictionary* ioOpts = null;
            ffmpeg.av_dict_set(&ioOpts, "protocol_whitelist", "rtp,udp", 0);
            ret = ffmpeg.avio_open2(&fmtCtx->pb, url, ffmpeg.AVIO_FLAG_WRITE, null, &ioOpts);
            ffmpeg.av_dict_free(&ioOpts);
            if (ret < 0) ThrowFfmpegError("avio_open2", ret);

            ret = ffmpeg.avformat_write_header(fmtCtx, null);
            if (ret < 0) ThrowFfmpegError("avformat_write_header", ret);

            // Write packets buffered during priming
            foreach (var pPtr in bufferedPackets)
            {
                var p = (AVPacket*)pPtr;
                p->stream_index = rtpStream->index;
                ffmpeg.av_packet_rescale_ts(p, codecCtx->time_base, rtpStream->time_base);
                ffmpeg.av_interleaved_write_frame(fmtCtx, p);
                ffmpeg.av_packet_free(&p);
            }
            bufferedPackets.Clear();

            // ── Encode loop ───────────────────────────────────────────────────
            int  frameCount = 0;
            long byteCount  = 0;
            var  sw         = Stopwatch.StartNew();

            // Manual IDR cadence — every `idrEvery` frames we tag the input as I-type so
            // the encoder produces a fresh IDR. Needed because h264_amf ignores gop_size
            // under usage=ultralowlatency. NVENC and QSV honor gop_size natively, so
            // this is gated to AMF only.
            bool needsForcedIdr = encoderName == "h264_amf";
            int  idrEvery       = Math.Max(1, codecCtx->gop_size);
            int  sinceIdr       = 0;

            while (!ct.IsCancellationRequested)
            {
                var acquireResult = duplication.AcquireNextFrame(100, out _, out var desktopResource);
                if (acquireResult == Vortice.DXGI.ResultCode.WaitTimeout) continue;
                acquireResult.CheckError();

                // Round-robin through the pool so each texture is idle for 2 frames
                // before being reused — enough time for the GPU encoder to finish reading it.
                int interIdx = texIdx;
                texIdx = (texIdx + 1) % PoolSize;

                pool.AcquireForCapture(interIdx);
                using (desktopResource)
                {
                    using var dxgiTex = desktopResource.QueryInterface<D3D11Tex2D>();
                    displayCtx.CopyResource(pool.DisplaySide[interIdx], dxgiTex);
                }
                duplication.ReleaseFrame().CheckError();
                pool.ReleaseAfterCapture(interIdx);

                pool.AcquireForEncode(interIdx);
                try
                {
                    var hwFrame = ffmpeg.av_frame_alloc();
                    if (hwFrame == null) continue;

                    hwFrame->format        = (int)AVPixelFormat.AV_PIX_FMT_D3D11;
                    hwFrame->hw_frames_ctx = ffmpeg.av_buffer_ref(hwFramesBuf);
                    hwFrame->data[0]       = (byte*)pool.EncoderSide[interIdx].NativePointer.ToPointer();
                    hwFrame->data[1]       = (byte*)(nuint)0;
                    hwFrame->width         = srcW;
                    hwFrame->height        = srcH;
                    hwFrame->pts           = pts++;
                    hwFrame->buf[0]        = ffmpeg.av_buffer_alloc(1);

                    hwFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
                    if (needsForcedIdr)
                    {
                        if (sinceIdr >= idrEvery)
                        {
                            hwFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
                            sinceIdr           = 0;
                        }
                        sinceIdr++;
                    }

                    if (ffmpeg.avcodec_send_frame(codecCtx, hwFrame) >= 0)
                    {
                        while (ffmpeg.avcodec_receive_packet(codecCtx, packet) == 0)
                        {
                            if ((packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0)
                                Console.WriteLine($"[Encode] IDR sent: size={packet->size}");
                            byteCount           += packet->size;
                            packet->stream_index = rtpStream->index;
                            ffmpeg.av_packet_rescale_ts(packet, codecCtx->time_base, rtpStream->time_base);
                            ffmpeg.av_interleaved_write_frame(fmtCtx, packet);
                            ffmpeg.av_packet_unref(packet);
                        }
                    }
                    ffmpeg.av_frame_free(&hwFrame);
                }
                finally
                {
                    pool.ReleaseAfterEncode(interIdx);
                }
                frameCount++;

                if (sw.ElapsedMilliseconds >= 1000)
                {
                    double kbps = byteCount * 8.0 / 1000.0;
                    Console.WriteLine($"[Encode] {frameCount} fps  {kbps:F0} kbps  {srcW}x{srcH} [GPU]");
                    frameCount = 0;
                    byteCount  = 0;
                    sw.Restart();
                }
            }

            // Flush
            ffmpeg.avcodec_send_frame(codecCtx, null);
            while (ffmpeg.avcodec_receive_packet(codecCtx, packet) == 0)
            {
                packet->stream_index = rtpStream->index;
                ffmpeg.av_packet_rescale_ts(packet, codecCtx->time_base, rtpStream->time_base);
                ffmpeg.av_interleaved_write_frame(fmtCtx, packet);
                ffmpeg.av_packet_unref(packet);
            }
            ffmpeg.av_write_trailer(fmtCtx);
        }
        finally
        {
            foreach (var pPtr in bufferedPackets)
            {
                var p = (AVPacket*)pPtr;
                ffmpeg.av_packet_free(&p);
            }
            pool?.Dispose();
            if (hwDevBuf    != null) { var b = hwDevBuf;    ffmpeg.av_buffer_unref(&b); }
            if (hwFramesBuf != null) { var b = hwFramesBuf; ffmpeg.av_buffer_unref(&b); }
            if (packet      != null) { var p = packet;      ffmpeg.av_packet_free(&p); }
            if (codecCtx    != null) { var c = codecCtx;    ffmpeg.avcodec_free_context(&c); }
            if (fmtCtx      != null)
            {
                if (fmtCtx->pb != null) ffmpeg.avio_closep(&fmtCtx->pb);
                ffmpeg.avformat_free_context(fmtCtx);
            }
            duplication?.Dispose();
            // Encoder device/context only need separate disposal in cross-adapter mode.
            if (!ReferenceEquals(encoderDevice, displayDevice))
            {
                encoderCtx?.Dispose();
                encoderDevice?.Dispose();
            }
            displayCtx?.Dispose();
            displayDevice?.Dispose();
            encoderAdapter?.Dispose();
            displayAdapter?.Dispose();
        }
    }

    // ─── CPU path ─────────────────────────────────────────────────────────────

    private void RunEncode(ChannelReader<FrameData> frames, CancellationToken ct)
    {
        AVCodecContext*  codecCtx = null;
        AVFormatContext* fmtCtx   = null;
        SwsContext*      swsCtx   = null;
        AVFrame*         yuvFrame = null;
        AVPacket*        packet   = null;

        try
        {
            var first = frames.ReadAsync(ct).AsTask().GetAwaiter().GetResult();
            int srcW  = first.Width;
            int srcH  = first.Height;
            int dstW  = int.Parse(_config.OutputWidth);
            int dstH  = int.Parse(_config.OutputHeight);

            Console.WriteLine($"[Encode] {srcW}x{srcH} → {dstW}x{dstH}, encoder={_config.Encoder}");

            // ── Codec ────────────────────────────────────────────────────────
            var encoder = DetectEncoder(_config.Encoder);
            var codec   = ffmpeg.avcodec_find_encoder_by_name(encoder);
            if (codec == null)
                throw new InvalidOperationException(
                    $"Encoder '{encoder}' not found. " +
                    "Ensure the encoder is available in your FFmpeg build.");

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (codecCtx == null) throw new OutOfMemoryException("avcodec_alloc_context3 failed");

            codecCtx->width     = dstW;
            codecCtx->height    = dstH;
            codecCtx->pix_fmt   = AVPixelFormat.AV_PIX_FMT_YUV420P;
            codecCtx->time_base = new AVRational { num = 1, den = _config.Framerate };
            codecCtx->framerate = new AVRational { num = _config.Framerate, den = 1 };
            codecCtx->bit_rate  = ParseBitrate(_config.Bitrate);
            codecCtx->gop_size  = _config.Framerate / 2;  // IDR every ~0.5 s → inline SPS/PPS
            codecCtx->flags    |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

            AVDictionary* encOpts = null;
            AddEncoderOptions(encoder, &encOpts);
            int ret = ffmpeg.avcodec_open2(codecCtx, codec, &encOpts);
            ffmpeg.av_dict_free(&encOpts);
            if (ret < 0) ThrowFfmpegError("avcodec_open2", ret);

            // ── RTP output ───────────────────────────────────────────────────
            string url = $"rtp://{_config.UdpIP}:{_config.UdpPort}";

            AVFormatContext* fmtCtxTmp = null;
            ret = ffmpeg.avformat_alloc_output_context2(&fmtCtxTmp, null, "rtp", url);
            if (ret < 0) ThrowFfmpegError("avformat_alloc_output_context2", ret);
            fmtCtx = fmtCtxTmp;

            var stream = ffmpeg.avformat_new_stream(fmtCtx, null);
            if (stream == null) throw new InvalidOperationException("avformat_new_stream failed");

            ret = ffmpeg.avcodec_parameters_from_context(stream->codecpar, codecCtx);
            if (ret < 0) ThrowFfmpegError("avcodec_parameters_from_context", ret);
            stream->time_base = codecCtx->time_base;

            AVDictionary* ioOpts = null;
            ffmpeg.av_dict_set(&ioOpts, "protocol_whitelist", "rtp,udp", 0);
            ret = ffmpeg.avio_open2(&fmtCtx->pb, url, ffmpeg.AVIO_FLAG_WRITE, null, &ioOpts);
            ffmpeg.av_dict_free(&ioOpts);
            if (ret < 0) ThrowFfmpegError("avio_open2", ret);

            ret = ffmpeg.avformat_write_header(fmtCtx, null);
            if (ret < 0) ThrowFfmpegError("avformat_write_header", ret);

            // ── Color converter: BGRA (from DXGI) → YUV420P (for H.264) ────
            swsCtx = ffmpeg.sws_getContext(
                srcW, srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
                dstW, dstH, AVPixelFormat.AV_PIX_FMT_YUV420P,
                2 /* SWS_BILINEAR */, null, null, null);
            if (swsCtx == null) throw new InvalidOperationException("sws_getContext failed");

            yuvFrame = ffmpeg.av_frame_alloc();
            if (yuvFrame == null) throw new OutOfMemoryException("av_frame_alloc failed");
            yuvFrame->width  = dstW;
            yuvFrame->height = dstH;
            yuvFrame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            ret = ffmpeg.av_frame_get_buffer(yuvFrame, 32);
            if (ret < 0) ThrowFfmpegError("av_frame_get_buffer", ret);

            packet = ffmpeg.av_packet_alloc();
            if (packet == null) throw new OutOfMemoryException("av_packet_alloc failed");

            long pts        = 0;
            int  frameCount = 0;
            long byteCount  = 0;
            var  sw         = Stopwatch.StartNew();

            byteCount += EncodeFrame(first, srcW, srcH, codecCtx, yuvFrame, packet, fmtCtx, stream, swsCtx, ref pts);
            first.Dispose();
            frameCount++;

            while (!ct.IsCancellationRequested)
            {
                FrameData frame;
                try
                {
                    frame = frames.ReadAsync(ct).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { break; }

                byteCount += EncodeFrame(frame, srcW, srcH, codecCtx, yuvFrame, packet, fmtCtx, stream, swsCtx, ref pts);
                frame.Dispose();
                frameCount++;

                if (sw.ElapsedMilliseconds >= 1000)
                {
                    double kbps = byteCount * 8.0 / 1000.0;
                    Console.WriteLine($"[Encode] {frameCount} fps  {kbps:F0} kbps  {dstW}x{dstH} [CPU]");
                    frameCount = 0;
                    byteCount  = 0;
                    sw.Restart();
                }
            }

            // Flush the encoder.
            ffmpeg.avcodec_send_frame(codecCtx, null);
            while (ffmpeg.avcodec_receive_packet(codecCtx, packet) == 0)
            {
                packet->stream_index = stream->index;
                ffmpeg.av_packet_rescale_ts(packet, codecCtx->time_base, stream->time_base);
                ffmpeg.av_interleaved_write_frame(fmtCtx, packet);
                ffmpeg.av_packet_unref(packet);
            }

            ffmpeg.av_write_trailer(fmtCtx);
        }
        finally
        {
            if (swsCtx   != null) ffmpeg.sws_freeContext(swsCtx);
            if (yuvFrame != null) { var f = yuvFrame; ffmpeg.av_frame_free(&f); }
            if (packet   != null) { var p = packet;   ffmpeg.av_packet_free(&p); }
            if (codecCtx != null) { var c = codecCtx; ffmpeg.avcodec_free_context(&c); }
            if (fmtCtx   != null)
            {
                if (fmtCtx->pb != null) ffmpeg.avio_closep(&fmtCtx->pb);
                ffmpeg.avformat_free_context(fmtCtx);
            }
        }
    }

    private static long EncodeFrame(
        FrameData src, int srcW, int srcH,
        AVCodecContext* codecCtx, AVFrame* yuvFrame, AVPacket* packet,
        AVFormatContext* fmtCtx, AVStream* stream, SwsContext* swsCtx,
        ref long pts)
    {
        fixed (byte* srcPtr = src.Pixels)
        {
            var srcData     = new byte_ptrArray4 { [0] = srcPtr };
            var srcLinesize = new int_array4     { [0] = srcW * 4 };
            ffmpeg.av_frame_make_writable(yuvFrame);
            ffmpeg.sws_scale(swsCtx, srcData, srcLinesize, 0, srcH,
                             yuvFrame->data, yuvFrame->linesize);
        }

        yuvFrame->pts = pts++;

        if (ffmpeg.avcodec_send_frame(codecCtx, yuvFrame) < 0) return 0;

        long bytes = 0;
        while (ffmpeg.avcodec_receive_packet(codecCtx, packet) == 0)
        {
            bytes += packet->size;
            packet->stream_index = stream->index;
            ffmpeg.av_packet_rescale_ts(packet, codecCtx->time_base, stream->time_base);
            ffmpeg.av_interleaved_write_frame(fmtCtx, packet);
            ffmpeg.av_packet_unref(packet);
        }
        return bytes;
    }

    private static void AddEncoderOptions(string encoder, AVDictionary** opts)
    {
        switch (encoder)
        {
            case "libx264":
                ffmpeg.av_dict_set(opts, "tune",   "zerolatency", 0);
                ffmpeg.av_dict_set(opts, "preset", "ultrafast",   0);
                break;
            case "h264_amf":
                ffmpeg.av_dict_set(opts, "usage",      "ultralowlatency", 0);
                ffmpeg.av_dict_set(opts, "quality",    "speed",           0);
                ffmpeg.av_dict_set(opts, "rc",         "cbr",             0);
                // h264_amf with usage=ultralowlatency ignores gop_size — IDRs only get
                // produced for the very first frame. forced_idr=1 makes I-frames promote
                // to IDR frames (with inline SPS/PPS) when we set pict_type below.
                ffmpeg.av_dict_set(opts, "forced_idr", "1",               0);
                break;
            case "h264_nvenc":
                ffmpeg.av_dict_set(opts, "preset", "p1",  0);
                ffmpeg.av_dict_set(opts, "tune",   "ll",  0);
                ffmpeg.av_dict_set(opts, "rc",     "cbr", 0);
                break;
            case "h264_qsv":
                ffmpeg.av_dict_set(opts, "preset",    "veryfast", 0);
                ffmpeg.av_dict_set(opts, "low_delay", "1",        0);
                break;
        }
    }

    private static long ParseBitrate(string s)
    {
        s = s.Trim().ToUpperInvariant();
        if (s.EndsWith('M')) return long.Parse(s[..^1]) * 1_000_000;
        if (s.EndsWith('K')) return long.Parse(s[..^1]) * 1_000;
        return long.Parse(s);
    }

    private static void ThrowFfmpegError(string call, int code)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(code, buf, 256);
        var msg = Marshal.PtrToStringAnsi((nint)buf) ?? $"error {code}";
        throw new InvalidOperationException($"{call} failed: {msg}");
    }

    public void Dispose() { /* resources freed in finally blocks */ }
}
