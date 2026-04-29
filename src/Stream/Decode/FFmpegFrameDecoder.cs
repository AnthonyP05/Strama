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

            while (!ct.IsCancellationRequested)
            {
                ret = ffmpeg.av_read_frame(fmtCtx, packet);
                if (ret < 0) break; // EOF or network closed

                if (packet->stream_index == videoIdx)
                    Decode(codecCtx, frame, packet, ref swsCtx);

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

    private void Decode(AVCodecContext* codecCtx, AVFrame* frame, AVPacket* packet, ref SwsContext* swsCtx)
    {
        // Send the compressed packet into the decoder. A negative return means the
        // packet was malformed — skip it rather than aborting the whole stream.
        if (ffmpeg.avcodec_send_packet(codecCtx, packet) < 0) return;

        // One packet can produce multiple decoded frames (e.g. after a flush).
        // avcodec_receive_frame returns 0 while frames are available.
        while (ffmpeg.avcodec_receive_frame(codecCtx, frame) == 0)
        {
            EmitFrame(frame, ref swsCtx);
            ffmpeg.av_frame_unref(frame); // release the frame's buffer back to the pool
        }
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
            ffmpeg.SWS_BILINEAR, null, null, null);

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
