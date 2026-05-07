using System.Runtime.InteropServices;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using Strama.Records;

namespace Strama.Decode;

/// <summary>
/// Decodes an H.264 RTP stream (described by an SDP file) into raw BGRA frames
/// using FFmpeg in-process — replaces the ffplay subprocess in UDPReceiver.
///
/// Before calling Run, point FFmpeg at its shared libraries:
///   ffmpeg.RootPath = @"C:\ffmpeg\bin";  // folder with avcodec-60.dll etc.
/// On Windows, download a shared build from https://www.gyan.dev/ffmpeg/builds/
/// </summary>
public sealed unsafe class FFmpegFrameDecoder : IFrameDecoder
{
    private readonly string _sdpPath;
    private readonly Channel<FrameData> _channel;

    public ChannelReader<FrameData> Frames => _channel.Reader;

    // Runs once the first time FFmpegFrameDecoder is used.
    // Sets the DLL search path and activates all FFmpeg function pointers.
    // Without Initialize(), every ffmpeg.* call throws NotSupportedException.
    static FFmpegFrameDecoder()
    {
        // Override via FFMPEG_ROOT env var, or place the DLLs next to the exe.
        // Download "release full shared" from https://www.gyan.dev/ffmpeg/builds/
        // and copy avcodec-60.dll, avformat-60.dll, avutil-58.dll,
        //          swscale-7.dll, swresample-4.dll into bin/Debug/net8.0/
        if (string.IsNullOrEmpty(ffmpeg.RootPath))
            ffmpeg.RootPath = Environment.GetEnvironmentVariable("FFMPEG_ROOT")
                           ?? AppContext.BaseDirectory;

        DynamicallyLoadedBindings.Initialize();
    }

    /// <param name="sdpPath">Path to the .sdp file describing the RTP stream.</param>
    /// <param name="capacity">Max frames queued before old ones are dropped.</param>
    public FFmpegFrameDecoder(string sdpPath, int capacity = 2)
    {
        _sdpPath = sdpPath;
        _channel = Channel.CreateBounded<FrameData>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>
    /// Opens the RTP stream and decodes frames until <paramref name="ct"/> is cancelled
    /// or the stream ends. Blocks the calling thread.
    /// </summary>
    public void Run(CancellationToken ct = default)
    {
        AVFormatContext* fmtCtx  = null;
        AVCodecContext*  codecCtx = null;
        SwsContext*      swsCtx  = null;
        AVFrame*         frame   = null;
        AVPacket*        packet  = null;

        try
        {
            // The SDP file contains rtp:// URIs, which are carried over udp://.
            // FFmpeg's protocol whitelist blocks any protocol chain not listed here.
            // probesize/analyzeduration cut the startup delay before the first frame.
            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "protocol_whitelist", "file,rtp,udp", 0);
            ffmpeg.av_dict_set(&opts, "probesize",          "32",           0);
            ffmpeg.av_dict_set(&opts, "analyzeduration",    "0",            0);
            // 10 MB socket receive buffer — without this the kernel drops fragments of large IDR
            // frames (~200 KB each, split into ~143 UDP datagrams) before the app reads them.
            ffmpeg.av_dict_set(&opts, "buffer_size",        "10485760",     0);

            int ret = ffmpeg.avformat_open_input(&fmtCtx, _sdpPath, null, &opts);
            ffmpeg.av_dict_free(&opts);
            if (ret < 0) throw new InvalidOperationException($"avformat_open_input: {AvError(ret)}");

            // Disable internal buffering — we want frames as soon as they arrive.
            fmtCtx->flags |= ffmpeg.AVFMT_FLAG_NOBUFFER;

            ret = ffmpeg.avformat_find_stream_info(fmtCtx, null);
            if (ret < 0) throw new InvalidOperationException($"avformat_find_stream_info: {AvError(ret)}");

            // Walk the streams to find the first video track and its decoder.
            int      videoIdx = -1;
            AVCodec* codec    = null;
            for (int i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                if (fmtCtx->streams[i]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
                    continue;
                videoIdx = i;
                codec    = ffmpeg.avcodec_find_decoder(fmtCtx->streams[i]->codecpar->codec_id);
                break;
            }

            if (videoIdx < 0)  throw new InvalidOperationException("No video stream found in SDP.");
            if (codec    == null) throw new InvalidOperationException("No decoder found for video codec.");

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (codecCtx == null) throw new OutOfMemoryException("avcodec_alloc_context3 returned null.");

            ffmpeg.avcodec_parameters_to_context(codecCtx, fmtCtx->streams[videoIdx]->codecpar);

            // LOW_DELAY tells the decoder not to buffer frames for reordering.
            // H.264 from a screen capture has no B-frames, so this is safe and cuts latency.
            codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

            ret = ffmpeg.avcodec_open2(codecCtx, codec, null);
            if (ret < 0) throw new InvalidOperationException($"avcodec_open2: {AvError(ret)}");

            frame  = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (frame == null || packet == null)
                throw new OutOfMemoryException("FFmpeg frame/packet alloc failed.");

            Console.WriteLine($"[Decode] Codec: {System.Runtime.InteropServices.Marshal.PtrToStringAnsi((nint)codec->name)}" +
                              $"  extradata={codecCtx->extradata_size}B" +
                              $"  w={codecCtx->width} h={codecCtx->height}");

            int pktCount   = 0;
            int frameCount = 0;
            int keyCount   = 0;
            var statTimer  = System.Diagnostics.Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                ret = ffmpeg.av_read_frame(fmtCtx, packet);
                if (ret < 0)
                {
                    Console.WriteLine($"[Decode] av_read_frame ended after {pktCount} pkts / {frameCount} frames: {AvError(ret)}");
                    break;
                }

                if (packet->stream_index == videoIdx)
                {
                    bool isKey = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                    if (isKey) keyCount++;

                    if (pktCount < 10 || isKey)
                    {
                        Console.Write($"[Decode] pkt#{pktCount} size={packet->size} key={isKey} data[0..8]=");
                        for (int i = 0; i < Math.Min(8, packet->size); i++)
                            Console.Write($"{packet->data[i]:X2} ");
                        Console.WriteLine();
                    }
                    pktCount++;
                    int decoded = Decode(codecCtx, frame, packet, ref swsCtx);
                    frameCount += decoded;
                    if (decoded > 0 && frameCount <= 3)
                        Console.WriteLine($"[Decode] First decoded frame #{frameCount}  fmt={frame->format}  {frame->width}x{frame->height}");
                }

                // Periodic summary: shows whether packets arrive but frames don't decode.
                if (statTimer.ElapsedMilliseconds >= 3000)
                {
                    Console.WriteLine($"[Decode] stats: {pktCount} pkts  {keyCount} key  {frameCount} decoded");
                    statTimer.Restart();
                }

                ffmpeg.av_packet_unref(packet);
            }

            // Send a null packet to flush any frames the decoder is still holding.
            ffmpeg.avcodec_send_packet(codecCtx, null);
            while (ffmpeg.avcodec_receive_frame(codecCtx, frame) == 0)
            {
                EmitFrame(frame, ref swsCtx);
                ffmpeg.av_frame_unref(frame);
            }
        }
        finally
        {
            if (swsCtx   != null) ffmpeg.sws_freeContext(swsCtx);
            if (frame    != null) { var f = frame;    ffmpeg.av_frame_free(&f); }
            if (packet   != null) { var p = packet;   ffmpeg.av_packet_free(&p); }
            if (codecCtx != null) { var c = codecCtx; ffmpeg.avcodec_free_context(&c); }
            if (fmtCtx   != null) ffmpeg.avformat_close_input(&fmtCtx);
            _channel.Writer.TryComplete();
        }
    }

    private int Decode(AVCodecContext* codecCtx, AVFrame* frame, AVPacket* packet, ref SwsContext* swsCtx)
    {
        int sendRet = ffmpeg.avcodec_send_packet(codecCtx, packet);
        if (sendRet < 0)
        {
            Console.WriteLine($"[Decode] avcodec_send_packet failed: {AvError(sendRet)}");
            return 0;
        }

        int count = 0;
        while (true)
        {
            int recvRet = ffmpeg.avcodec_receive_frame(codecCtx, frame);
            if (recvRet == 0)
            {
                EmitFrame(frame, ref swsCtx);
                ffmpeg.av_frame_unref(frame);
                count++;
                continue;
            }
            // -11 = AVERROR(EAGAIN): decoder needs more packets before it can output a frame.
            // This is normal — it will keep returning EAGAIN until it receives an IDR keyframe.
            // AVERROR_EOF is also normal (stream ended). Log everything else as a real error.
            if (recvRet != -11 && recvRet != ffmpeg.AVERROR_EOF)
                Console.WriteLine($"[Decode] receive_frame error: {AvError(recvRet)} ({recvRet})");
            break;
        }
        return count;
    }

    private void EmitFrame(AVFrame* frame, ref SwsContext* swsCtx)
    {
        int w = frame->width;
        int h = frame->height;

        // sws_getCachedContext reuses the converter if format/size haven't changed,
        // or transparently creates a new one on the first call or after a resize.
        // H.264 decoded output is typically YUV420P; we convert it to BGRA to match
        // the pixel format produced by DxgiScreenCapturer on the server.
        swsCtx = ffmpeg.sws_getCachedContext(
            swsCtx,
            w, h, (AVPixelFormat)frame->format,
            w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
            2 /* SWS_BILINEAR */, null, null, null);

        if (swsCtx == null) return;

        var pixels = new byte[w * h * 4];
        fixed (byte* dstPtr = pixels)
        {
            // BGRA is a packed single-plane format, so only slot [0] is needed.
            var dstData     = new byte_ptrArray4 { [0] = dstPtr };
            var dstLinesize = new int_array4     { [0] = w * 4  };
            ffmpeg.sws_scale(swsCtx, frame->data, frame->linesize, 0, h, dstData, dstLinesize);
        }

        _channel.Writer.TryWrite(new FrameData(pixels, w, h));
    }

    private static string AvError(int code)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(code, buf, 256);
        return Marshal.PtrToStringAnsi((nint)buf) ?? $"error {code}";
    }

    public void Dispose() { /* all resources are freed in Run's finally block */ }
}
