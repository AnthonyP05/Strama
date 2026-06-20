# Strama — Project Guide for Codex

## What is this project?

A **remote desktop tool** written in C# (.NET 8), similar to TeamViewer / AnyDesk but custom-built. **Single Avalonia application**: every install is symmetrically a host *and* a client. Each instance shows its own short code; pasting a peer's code triggers a connect request that the remote user accepts or denies. After accept, the entering user views the accepting user's screen.

```
[Peer A — any instance]                    [Peer B — any instance]
TcpListener: 8888 ◄─── connect-request ─── User typed Peer A's code
"View my screen?" prompt                   Awaiting accept...
User clicks Allow ─── HandshakeResponse ─► Receives accept
RtpFrameEncoder starts                     FFmpegFrameDecoder starts
DXGI capture → h264_amf/nvenc/qsv          FFmpeg.AutoGen → BGRA
     or libx264 (CPU fallback)
     ──── RTP/UDP stream ─────────────►    WriteableBitmap → Avalonia Image
```

Every peer always runs the inbound TCP listener. Whichever peer initiates a session becomes the viewer; the other becomes the host. Single session at a time per instance.

## End goal — what the user is building

A working low-latency remote desktop:
1. Peer captures + encodes screen, streams RTP/UDP to whoever it accepts a request from
2. Peer decodes + displays the remote screen in the Avalonia UI
3. (Future) Viewer captures mouse/keyboard → sends back to host over TCP
4. (Future) Host simulates the input (Windows: `SendInput`, Linux: `XTest`)
5. (Future) Cross-platform host capture (Linux X11, macOS CoreGraphics)
6. (Future) Rendezvous server so codes work across networks instead of LAN-only

## Project structure

```
strama.csproj                         (Avalonia Exe, net8.0, single project)
src/
  Program.cs                          ✅ Avalonia entry; --console flag drops to Phase-1 console mode
  Stream/                             pipeline-side code (capture / encode / decode / transport)
    Records/
      HandshakeMsg.cs                 ✅ HandshakeConfig + HandshakeResponse
      FrameData.cs                    ✅ readonly struct, _pooled flag for ArrayPool
    Capture/
      IScreenCapturer.cs, ScreenCapturerFactory.cs   ✅
      CaptureSession.cs               ✅ capture loop → bounded Channel<FrameData>
      Windows/DxgiScreenCapturer.cs   ✅ working
      Linux/X11ScreenCapturer.cs      ❌ placeholder
      MacOS/CoreGraphicsScreenCapturer.cs ❌ placeholder
    Encode/
      IFrameEncoder.cs, RtpFrameEncoder.cs  ✅ GPU (D3D11 → h264_amf/nvenc/qsv) + CPU (libx264) paths
      AdapterSelector.cs                  ✅ vendor-aware encoder availability pre-flight
    Decode/
      IFrameDecoder.cs, FFmpegFrameDecoder.cs ✅ RTP/SDP → H.264 → BGRA
    Network/
      Tcp/HandshakeProtocol.cs        ✅ wire format: HandshakeRequest{Magic,UdpPort} → HandshakeResponse
      UDP/UDPReceiver.cs              ✅ exposes ChannelReader<FrameData> via Session record
  UI/                                 Avalonia GUI (added in Phases 2–6)
    App.axaml, App.axaml.cs           ✅ SemiTheme dark, DataTemplates for VM→View mapping
    Views/
      MainWindow.axaml                ✅ ContentControl + modal overlays (Settings, IncomingRequest)
      HomeView.axaml                  ✅ local code badge + copy button + connect input + settings cog
      ConnectingView.axaml            ✅ transient "Connecting…" with cancel
      IncomingRequestView.axaml       ✅ accept/deny modal with 30 s auto-deny countdown
      HostingView.axaml               ✅ "X is viewing your screen" + disconnect
      ViewingView.axaml               ✅ full-bleed Image bound to WriteableBitmap + HUD + Esc-to-disconnect
      SettingsView.axaml              ✅ bitrate / fps / resolution / encoder
    ViewModels/
      ViewModelBase.cs                ✅ ObservableObject from CommunityToolkit.Mvvm
      MainViewModel.cs                ✅ orchestrator, swaps CurrentView from ConnectionManager events
      HomeViewModel, ConnectingViewModel, IncomingRequestViewModel,
      HostingViewModel, ViewingViewModel, SettingsViewModel  ✅
    Services/
      ConnectionManager.cs            ✅ state machine (Idle/IncomingRequest/Connecting/Hosting/Viewing)
      IPeerResolver.cs + IpEndpointResolver.cs   ✅ v1: code = IP[:port]
      ClientSettings.cs               ✅ POCO, .ToHandshakeConfig() builds the wire record
      SettingsStore.cs                ✅ JSON at %APPDATA%\Strama\config.json
      FrameRenderer.cs                ✅ single WriteableBitmap, FrameReady event for invalidation
      NetworkUtilities.cs             ✅ GetLocalIPv4
archive/                              kept for reference, excluded from build via <Compile Remove>
  TCPServer.cs, TCPClient.cs          legacy console-Main wrappers
  UDPSender.cs, PipeFrameSender.cs    legacy ffmpeg-subprocess senders
```

## What's working end-to-end

The app launches into HomeView showing the local LAN IPv4 as the code. Pairing, both directions of accept/deny, hosting, viewing with live frame rendering, settings persistence, and disconnect (from either side) all work. The `--console host` / `--console <ip>` flags retain the Phase-1 headless behavior for regression testing the transport layer without the GUI.

### `RtpFrameEncoder` (host side)
- **GPU path** (`RunEncodeGpu`): zero-copy DXGI→D3D11 texture pool→h264_amf/nvenc/qsv→RTP. Uses `AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA` and `AV_PIX_FMT_D3D11`. Capture and encode share one D3D11 device on the adapter owning the display output (selected by `DriverType.Hardware`).
- **CPU path** (`RunEncode`): captures via `CaptureSession`, BGRA→YUV420P via `sws_scale`, libx264→RTP. Used when no GPU encoder is available on this hardware (e.g. hybrid laptops where the dGPU isn't driving the display).
- **Encoder selection** (`DetectEncoder` + `AdapterSelector`): `auto` walks `h264_amf → h264_nvenc → h264_qsv → libx264`, picking the first whose vendor adapter exists AND owns a display output. Explicit choices (e.g. `h264_nvenc`) are honored if their adapter is available, else fall back to `libx264` with a console message.
- **Priming**: encodes a frame before opening the RTP muxer so `extradata` (SPS/PPS) is populated for `avformat_write_header`.
- **AMF-only forced IDR cadence**: every `gop_size` frames, `hwFrame->pict_type = AV_PICTURE_TYPE_I` is set — but only for `h264_amf`. Required because h264_amf with `usage=ultralowlatency` ignores `gop_size`. Combined with `forced_idr=1` (also AMF-only) so I-frames promote to true IDR with inline SPS/PPS. NVENC and QSV honor `gop_size` natively and don't get the override.

### `FFmpegFrameDecoder` (viewer side)
- Opens an SDP file describing the RTP/UDP stream
- 10 MB UDP receive buffer (`buffer_size=10485760`) — needed because IDR frames at 1440p are ~420 KB, fragmented into ~300 UDP datagrams that overflow the default ~64 KB buffer
- `AV_CODEC_FLAG_LOW_DELAY` — no B-frame reordering (screen capture has no B-frames)
- Decodes to YUV420P → `sws_scale` → BGRA → bounded `Channel<FrameData>` with `DropOldest`
- Verbose diagnostic logging currently enabled (NAL type scan, IDR detection, periodic stats) — leftover from debugging, candidate for cleanup

### Connection lifecycle (`ConnectionManager`)
- `StartListening()` binds the TCP port at app startup
- Inbound: accept → `HandshakeProtocol.AcceptAsync` (which fires `IncomingRequestReceived` while it awaits the user's accept/deny) → on accept, builds effective `HandshakeConfig` from `_hostTemplateProvider()` (which reads live `ClientSettings`) and starts the encoder
- Outbound: `RequestConnectionAsync(code, template)` resolves code via `IPeerResolver`, opens TCP, sends `HandshakeRequest{Magic, UdpPort}`, awaits response, starts the decoder
- **Single teardown path** (`TearDownSessionAsync`, guarded by `Interlocked.Exchange`): `Disconnect()`, `MonitorHostSessionAsync`, and `MonitorViewerSessionAsync` all funnel through it so concurrent races resolve cleanly. Viewer's monitor races the decoder against a TCP-closed watcher so when the host disconnects, the viewer's UI returns to Home immediately even though the FFmpeg decoder is still blocked on UDP.

### Handshake wire format
- Requester → JSON `HandshakeRequest { Magic = "Strama-v1", UdpPort }`
- Accepter → JSON `HandshakeResponse { Accepted, Config? }` — `Config` carries the encoder settings the host will use, plus `UdpIP/UdpPort` filled with the requester's TCP remote IP and the requested UDP port (so the decoder's SDP and the encoder's RTP destination match without a separate IP exchange)
- During session: viewer either sends `"disconnect"` (host responds `"ok"`) or just closes the socket; host either gets `"disconnect"` or sees a closed socket. Either way `WaitForDisconnectAsync` returns and teardown runs.

## Current problems

### 1. Decoder thread leaks on disconnect *(new — flagged for a later session)*
- When a session ends, `_sessionCts.Cancel()` is set on the viewer side, but `FFmpegFrameDecoder.Run` is blocked inside `av_read_frame` waiting on UDP. The cancellation token is only checked at the top of each loop iteration, so until `av_read_frame` returns (which it might never do, since UDP just goes silent), the decoder thread sits idle in the background.
- The UI is correct — `MonitorViewerSessionAsync` watches the TCP socket and triggers teardown the moment the host drops, so the viewer's window returns to Home immediately.
- Each session leaves one stuck decoder thread + held UDP socket. Doesn't break anything user-visible at modest session counts but is a real leak.
- **Fix**: set FFmpeg's `interrupt_callback` on the `AVFormatContext` (and on each opened protocol) to a function that returns 1 when our cancellation token is cancelled. FFmpeg polls it during blocking I/O and bails. Implementation goes in `FFmpegFrameDecoder.Run` right after `avformat_alloc_context` (we currently use `avformat_open_input` which allocates internally — switch to allocate-then-set-callback-then-open). Alternative: set `rw_timeout` / `stimeout` so blocked reads time out periodically, but that's coarser.

### 2. Forced-IDR approach is bandwidth-wasteful
- IDRs at 1440p are ~420 KB each; at `gop_size = framerate/2` you send one every ~0.5–1 s
- That's ~3–5 Mbps of pure IDR overhead carrying mostly-unchanged pixels
- Causes visible bitrate spikes (8 → 13+ Mbps swings) — exactly what gets dropped on a real network

### 3. The 10 MB UDP receive buffer is masking, not solving
- Without it, IDR fragments overflow the OS buffer and the RTP demuxer discards the partial IDR
- Works on loopback but is fragile on a real network with real packet loss
- Intra-refresh (see optimizations) would eliminate the need entirely

### 4. Race condition on the very first IDR
- Host sends the priming IDR immediately after `avformat_write_header`
- Viewer's UDP socket may not be open yet → first IDR is dropped
- Currently masked by the periodic forced-IDRs (next one comes within ~0.5 s)

### 5. Hardcoded / under-versioned handshake
- `HandshakeRequest.Magic = "Strama-v1"` is the only protocol gate. No feature negotiation, no graceful version mismatch error path beyond a deserialize failure.
- Will need real versioning before mixing instances on different builds.

### 6. No input reverse channel yet
- Viewer doesn't capture or send mouse/keyboard
- Host doesn't simulate input

### 7. Verbose diagnostic logging
- `FFmpegFrameDecoder` prints NAL types per packet, IDR markers, periodic stats
- `RtpFrameEncoder` prints "IDR sent: size=…" per IDR
- Useful while debugging, noise in production. Should be gated behind a debug flag.

### 8. Forced-IDR fix is GPU-path only
- `RunEncodeGpu` has the manual `pict_type = I` cadence
- `RunEncode` (CPU/libx264) doesn't — libx264 respects `gop_size` natively, but the inconsistency means switching encoders has different IDR behavior

### 9. `probesize=32` is unreasonably small
- FFmpeg warns: `Stream #0: not enough frames to estimate rate; consider increasing probesize`
- Doesn't break decoding (we set `extradata` from the SDP), but the warning is noise.

### 10. Single monitor hardcoded
- `DxgiScreenCapturer` and `RtpFrameEncoder.RunEncodeGpu` both use `EnumOutputs(0, …)` — primary display only.

### 11. LAN-only by default
- `IpEndpointResolver` (the v1 `IPeerResolver`) treats codes as raw `IP[:port]`. WAN works only with manual port forwarding.
- The `IPeerResolver` interface is the seam where a rendezvous-server-backed implementation can drop in later without touching `ConnectionManager`.

### 12. `auto` encoder probes hardware availability, falls back to libx264 ✅ FIXED
- `DetectEncoder` now consults `AdapterSelector.IsHardwareAvailable(name)` for each candidate, which checks both that the codec is present in the FFmpeg build AND that a matching DXGI adapter exists and owns a display output.
- Implementation: `src/Stream/Encode/AdapterSelector.cs` — vendor IDs (AMD `0x1002`, NVIDIA `0x10DE`, Intel `0x8086`) and DXGI factory enumeration.
- Behavior: on an NVIDIA-only desktop, `auto` skips `h264_amf` and picks `h264_nvenc`. On an Intel-only system, picks `h264_qsv`. With no GPU encoder present, falls through to `libx264`.

### 13. GPU encode requires capture and encode on the same adapter
- `RunEncodeGpu` creates one D3D11 device via `DriverType.Hardware`, which picks the adapter owning the display output. DuplicateOutput must run on that adapter; the encoder reads from the same device's VRAM.
- This works for: single-GPU desktops of any vendor (AMD, NVIDIA, Intel), and laptops where the encoder vendor's adapter is the one driving the display.
- This does NOT work for: hybrid laptops where the iGPU drives the display but you want to encode on the dGPU (e.g. Intel display + NVIDIA NVENC). D3D11 cross-adapter texture sharing between vendors was attempted (both NT-handle + keyed-mutex and legacy `SHARED_KEYEDMUTEX` paths) and failed at `OpenSharedResource(1)` with `E_INVALIDARG` on the Intel↔NVIDIA combination — driver support for cross-vendor D3D11 sharing is patchy.
- Current behavior on hybrid laptops: `IsHardwareAvailable` uses `HasAdapterWithOutput`, so an encoder whose vendor's adapter has no displays is treated as unavailable. `auto` falls through to `libx264` (CPU). Explicit selection of `h264_nvenc` on such a laptop also falls back to `libx264` with a console message.
- Future path: switch capture from DXGI Desktop Duplication to Windows.Graphics.Capture (WGC), which takes any D3D11 device and lets the system compositor handle cross-adapter copies internally. Sidesteps D3D11 shared-resource entirely. ~200 LoC capture-layer rewrite, Win10 1903+ only.

### 13b. AMF-only forced IDR cadence ✅ FIXED
- The manual `pict_type = AV_PICTURE_TYPE_I` cadence (workaround for `h264_amf` ignoring `gop_size` under `ultralowlatency`) is now gated by `encoderName == "h264_amf"`. NVENC and QSV honor `gop_size` natively, so they no longer get redundant work.
- Same gate applies to `forced_idr=1` in `AddEncoderOptions` (already AMF-only via the switch case).

### 14. "Native" resolution breaks the host *(reported, unconfirmed in code yet)*
- Selecting "Native (no resize)" in Settings writes `OutputWidth = 0, OutputHeight = 0` to `ClientSettings`.
- The CPU `RunEncode` does `int.Parse(_config.OutputWidth)` which yields `0`, then sets `codecCtx->width = 0; codecCtx->height = 0` — `avcodec_open2` will reject this, and the host shows "no resolution set" before any video flows.
- The GPU path ignores `OutputWidth/Height` (it always uses native capture dims), so Native works there.
- Fix: in `RunEncode`, when `OutputWidth/Height` are zero, fall back to the first frame's capture dimensions before opening the codec.

### 15. Cannot immediately reconnect after disconnect *(reported, suspected hanging connection)*
- After tearing down a session, an immediate reconnect attempt from the same peer fails.
- Suspected cause: a TCP socket or listener-side state not fully released by `TearDownSessionAsync` before the next inbound connection arrives. `_sessionTcp?.Dispose()` runs, but the OS may hold the socket in TIME_WAIT, or the previous `RtpFrameEncoder` task hasn't fully returned and the FFmpeg encoder context still owns the UDP socket on the host side.
- Needs investigation: log when each cleanup step completes and reproduce.

### 16. No live bitrate readout on viewer; active encoder not surfaced
- The encoder prints `[Encode] {fps} {kbps}` to the console but the viewer's `ViewingView` HUD doesn't show bitrate at all.
- Requested: live bitrate + 30-second moving average on the viewer.
- Also requested: when `auto` resolves an encoder, surface which codec was picked (and announce if it ever switches mid-stream — though we don't currently switch).
- Stats source should be the decoder side, measured off the packets/bytes coming in (not piped from the encoder), so it reflects what's actually arriving over the network.

## Possible optimizations

### Bandwidth / latency
- **Long GOP + on-demand IDR** (recommended next): set `gop_size = framerate * 5` (or higher), and have the viewer request an IDR over the TCP back-channel on connect or detected loss. Reuses the input-event channel that'll exist later.
- **Intra-refresh** (`intra_refresh_mb` for h264_amf): each frame contains a small slice of intra-coded macroblocks; after one full cycle the whole picture has refreshed. No IDR spikes, uniform packet sizes, naturally resilient to small packet loss. Best fit for remote desktop. Slight quality hit in the refresh band.
- **Cap IDR size** (`max_au_size`, or higher `qp_i`): if you keep periodic IDRs, limit how big they get so they fit in fewer UDP fragments.
- **Adaptive bitrate**: detect loss via RTCP receiver reports (or homegrown signal over TCP) and adjust `bit_rate` dynamically.

### Decode path
- **Hardware decode on the viewer** (D3D11VA / DXVA2 / NVDEC / VideoToolbox): currently CPU-decoded via libavcodec. At 1440p+ this saves real CPU. `AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA` mirrors what the encoder side already does.
- **Skip the YUV→BGRA conversion**: if Avalonia can render NV12 directly via a shader, you save a `sws_scale` per frame (~3–5 ms at 1440p).

### Memory / GC
- **`ArrayPool<byte>` for `FrameData.Pixels`**: at 60–150 fps each frame allocates a fresh `byte[w*h*4]` (~14 MB at 1440p). Pooling drops GC pressure substantially. The struct already has a `_pooled` flag for this.

### Protocol
- **Versioned, typed handshake**: extend `HandshakeRequest` / `HandshakeResponse` with a real version field and explicit error reason codes; `Magic = "Strama-v1"` becomes one entry in a negotiation table.
- **Same TCP channel for control + input + IDR requests**: avoids opening more sockets.

### Robustness
- **Drop the 10 MB UDP buffer once intra-refresh is in place**: no more giant IDRs, default buffer is fine.
- **Handle `AccessLost` from DXGI**: required for session switch, lock screen, UAC dialog. Currently the encoder will throw and exit.
- **Reconnect logic**: today, a dropped TCP session just returns the user to Home with a banner — no auto-retry.
- **Server selects port automatically if 8888 is taken**: today the second instance on one machine has to be invoked with `--tcp <other>`.

### Cross-platform
- **Implement `X11ScreenCapturer`** (Linux): use XCB/XShm for capture; `xtest` for input simulation.
- **Implement `CoreGraphicsScreenCapturer`** (macOS): `CGDisplayCreateImage` for capture; `CGEventPost` for input.
- **GPU encode path is Windows-only**: Linux would need VAAPI (`h264_vaapi`) + DRM Prime; macOS would need VideoToolbox.

### Hybrid-GPU laptops (Windows)
- **Switch capture to Windows.Graphics.Capture (WGC)** so capture and encode can live on different adapters without D3D11 cross-adapter texture sharing. WGC takes any D3D11 device and the system compositor handles the cross-adapter copy internally. Sidesteps the entire `OpenSharedResource` / keyed-mutex problem space.
- Today hybrid laptops fall back to libx264 — see problem #13 for why D3D11 cross-vendor sharing was abandoned. WGC is the documented modern alternative; ~200 LoC capture-layer rewrite. Win10 1903+, which is fine for the project's targets.
- Migration path: replace `DxgiScreenCapturer` with a `WgcScreenCapturer` behind the existing `IScreenCapturer` interface. The encoder's same-adapter assumption can stay — WGC handles the bridging upstream.

### Pairing UX
- **Rendezvous server** for cross-network pairing without port forwarding. Implement `IPeerResolver` with an HTTP-backed code lookup. Eventually with NAT traversal (STUN/TURN).
- **Friendlier code format**: encode `IP+port` as a 8–10 char base32 string instead of raw dotted IP. The `IPeerResolver` interface is already the right seam.

## Design decisions made

- **Single Avalonia project, not multi-project split** — every install is symmetrically host *and* client; a server/client binary split would defeat the peer-to-peer model. (Original three-project plan was reconsidered after the user clarified the UX.)
- **Avalonia 11.2.1 + Semi.Avalonia (dark) + CommunityToolkit.Mvvm + Inter font** — chosen for "sleek and modern" cross-platform without screaming "I'm a Microsoft app."
- **Compiled bindings on by default** (`AvaloniaUseCompiledBindingsByDefault`) — every view declares `x:DataType` for compile-time binding errors.
- **Modal-overlay pattern** (settings, incoming-request) over `MainViewModel.CurrentView` swap (home / connecting / hosting / viewing). Both layered in `MainWindow`.
- **Settings mutated in place** rather than swapping references on save — `ConnectionManager`'s `_hostTemplateProvider` closes over the same `ClientSettings` instance the GUI edits, so a save takes effect on the next session without re-wiring anything.
- **Single `TearDownSessionAsync` for all session-end paths**, gated by `Interlocked.Exchange` so the racing paths (user disconnect, decoder ended, host TCP closed, viewer TCP closed) never run cleanup twice.
- **Vortice.Windows** over SharpDX (abandoned) and raw P/Invoke
- **`FFmpeg.AutoGen`** for in-process decode (not `FFMpegCore` — subprocess only; that package has been removed)
- **`usage=ultralowlatency`** for h264_amf is correct for remote desktop, despite the gop_size quirk
- **Bounded `Channel<FrameData>` with `DropOldest`** — capture/decode shouldn't backlog; if the consumer is slow, prefer freshness over completeness
- **RTP over UDP** (not raw H.264 over TCP) — UDP for low latency, RTP for fragmentation/reassembly of large NALs
- **WriteableBitmap allocated once** per resolution, not per frame — `FrameReady` event nudges Avalonia to redraw via `Image.InvalidateVisual()` since binding propagation alone doesn't pick up content-only changes

## NuGet packages

| Package | Version | Purpose |
|---|---|---|
| Avalonia | 11.2.1 | GUI framework (cross-platform XAML) |
| Avalonia.Desktop | 11.2.1 | Desktop runtime |
| Avalonia.Themes.Fluent | 11.2.1 | Base theme (transitive dep of Semi) |
| Avalonia.Fonts.Inter | 11.2.1 | Bundled Inter font |
| Avalonia.Diagnostics | 11.2.1 | Debug-only inspector (F12) |
| Semi.Avalonia | 11.2.1.4 | Modern theme |
| CommunityToolkit.Mvvm | 8.3.2 | `[ObservableProperty]` / `[RelayCommand]` source generators |
| FFmpeg.AutoGen | 8.0.0 | In-process FFmpeg bindings (encode + decode) |
| System.Text.Json | 10.0.5 | Handshake serialization |
| Vortice.Direct3D11 | 3.8.3 | D3D11 device + texture API |
| Vortice.DXGI | 3.8.3 | Desktop Duplication API |

FFmpeg shared libraries must sit next to the executable or be pointed at via `FFMPEG_ROOT` env var. `FFmpeg.AutoGen 8.0.0` binds to the FFmpeg 8.x ABI, which means the **62-series** DLLs:

- `avcodec-62.dll`
- `avformat-62.dll`
- `avutil-60.dll`
- `swscale-9.dll`
- `swresample-6.dll`

Use the "release full shared" build from gyan.dev. `avdevice` and `avfilter` are not needed (we don't open FFmpeg input devices or use the filter graph).

## Suggested next steps

In rough order of impact for the user's stated goal:

1. **Fix the decoder thread leak** (problem #1) — set FFmpeg's `interrupt_callback` so cancellation actually unblocks `av_read_frame`. Self-contained fix in `FFmpegFrameDecoder.Run`.
2. **Input reverse channel** — capture Avalonia pointer/keyboard events on the viewer, serialize, send over the existing TCP socket; host invokes `SendInput`. The `HandshakeProtocol` TCP channel is the natural place for this.
3. **Long GOP + on-demand IDR** — once the TCP back-channel exists for input, piggyback "request keyframe" messages on it. Drops bandwidth significantly.
4. **WGC capture** (problem #13 follow-up) — switch from DXGI Desktop Duplication to Windows.Graphics.Capture so hybrid laptops can NVENC instead of falling back to libx264.
5. **Cleanup pass** — gate diagnostic logging behind a debug flag, ship `<OutputType>WinExe</OutputType>` so there's no console window in release builds.
6. **Robustness** — DXGI `AccessLost` recovery, viewer reconnect on dropped TCP, per-session error reporting.
7. **Cross-platform capture** — Linux first (X11 is more straightforward than macOS CoreGraphics).
8. **Rendezvous server** — for cross-network codes without port forwarding. The `IPeerResolver` interface is the seam.
