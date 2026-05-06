using System.Runtime.InteropServices;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using Strama.Capture;
using Strama.Records;

namespace Strama.Encode;

/// <summary>
/// Captures the screen and encodes frames to H.264 entirely in-process using
/// FFmpeg.AutoGen, then sends them over RTP/UDP. No ffmpeg subprocess or pipe.
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
        try
        {
            RunCapture(ct);
        }
        catch (OperationCanceledException) { /* clean shutdown */ }
    }

    private void RunCapture(CancellationToken ct)
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

    private void RunEncode(ChannelReader<FrameData> frames, CancellationToken ct)
    {
        AVCodecContext*  codecCtx = null;
        AVFormatContext* fmtCtx   = null;
        SwsContext*      swsCtx   = null;
        AVFrame*         yuvFrame = null;
        AVPacket*        packet   = null;

        try
        {
            // First frame tells us the source dimensions from DXGI.
            var first = frames.ReadAsync(ct).AsTask().GetAwaiter().GetResult();
            int srcW  = first.Width;
            int srcH  = first.Height;
            int dstW  = int.Parse(_config.OutputWidth);
            int dstH  = int.Parse(_config.OutputHeight);

            Console.WriteLine($"[Encode] {srcW}x{srcH} → {dstW}x{dstH}, encoder={_config.Encoder}");

            // ── Codec ────────────────────────────────────────────────────────
            var codec = ffmpeg.avcodec_find_encoder_by_name(_config.Encoder);
            if (codec == null)
                throw new InvalidOperationException(
                    $"Encoder '{_config.Encoder}' not found. " +
                    "Ensure the encoder is available in your FFmpeg build.");

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (codecCtx == null) throw new OutOfMemoryException("avcodec_alloc_context3 failed");

            codecCtx->width     = dstW;
            codecCtx->height    = dstH;
            codecCtx->pix_fmt   = AVPixelFormat.AV_PIX_FMT_YUV420P;
            codecCtx->time_base = new AVRational { num = 1, den = _config.Framerate };
            codecCtx->framerate = new AVRational { num = _config.Framerate, den = 1 };
            codecCtx->bit_rate  = ParseBitrate(_config.Bitrate);
            codecCtx->gop_size  = 30;
            // Don't buffer frames for reordering — output each packet immediately.
            codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

            AVDictionary* encOpts = null;
            AddEncoderOptions(_config.Encoder, &encOpts);
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

            // Open the UDP socket.
            AVDictionary* ioOpts = null;
            ffmpeg.av_dict_set(&ioOpts, "protocol_whitelist", "rtp,udp", 0);
            ret = ffmpeg.avio_open2(&fmtCtx->pb, url, ffmpeg.AVIO_FLAG_WRITE, null, &ioOpts);
            ffmpeg.av_dict_free(&ioOpts);
            if (ret < 0) ThrowFfmpegError("avio_open2", ret);

            ret = ffmpeg.avformat_write_header(fmtCtx, null);
            if (ret < 0) ThrowFfmpegError("avformat_write_header", ret);

            // ── Color converter: BGRA (from DXGI) → YUV420P (for H.264) ────
            // Also handles the resize from capture resolution to output resolution.
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

            long pts = 0;

            // Encode the first frame we already read.
            EncodeFrame(first, srcW, srcH, codecCtx, yuvFrame, packet, fmtCtx, stream, swsCtx, ref pts);
            first.Dispose();

            while (!ct.IsCancellationRequested)
            {
                FrameData frame;
                try
                {
                    frame = frames.ReadAsync(ct).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { break; }

                EncodeFrame(frame, srcW, srcH, codecCtx, yuvFrame, packet, fmtCtx, stream, swsCtx, ref pts);
                frame.Dispose();
            }

            // Flush the encoder: send null frame to drain any buffered packets.
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

    private static void EncodeFrame(
        FrameData src, int srcW, int srcH,
        AVCodecContext* codecCtx, AVFrame* yuvFrame, AVPacket* packet,
        AVFormatContext* fmtCtx, AVStream* stream, SwsContext* swsCtx,
        ref long pts)
    {
        // Convert BGRA → YUV420P and resize in one sws_scale call.
        fixed (byte* srcPtr = src.Pixels)
        {
            var srcData     = new byte_ptrArray4 { [0] = srcPtr };
            var srcLinesize = new int_array4     { [0] = srcW * 4 };
            ffmpeg.av_frame_make_writable(yuvFrame);
            ffmpeg.sws_scale(swsCtx, srcData, srcLinesize, 0, srcH,
                             yuvFrame->data, yuvFrame->linesize);
        }

        yuvFrame->pts = pts++;

        // Send the frame into the encoder. Negative return = encoder busy, skip.
        if (ffmpeg.avcodec_send_frame(codecCtx, yuvFrame) < 0) return;

        // A single frame can produce multiple packets (especially at keyframes).
        while (ffmpeg.avcodec_receive_packet(codecCtx, packet) == 0)
        {
            packet->stream_index = stream->index;
            // Rescale PTS/DTS from codec timebase to stream timebase before muxing.
            ffmpeg.av_packet_rescale_ts(packet, codecCtx->time_base, stream->time_base);
            ffmpeg.av_interleaved_write_frame(fmtCtx, packet);
            ffmpeg.av_packet_unref(packet);
        }
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
                ffmpeg.av_dict_set(opts, "usage",   "ultralowlatency", 0);
                ffmpeg.av_dict_set(opts, "quality", "speed",           0);
                ffmpeg.av_dict_set(opts, "rc",      "cbr",             0);
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

    public void Dispose() { /* resources freed in RunEncode's finally block */ }
}
