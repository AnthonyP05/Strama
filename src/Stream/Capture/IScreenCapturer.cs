/*

IScreenCapturer → pipes raw BGRA 
frames to FFmpeg stdin (-f rawvideo -pix_fmt bgra -i pipe:0). 
FFmpeg still handles encode + RTP streaming for now.

*/

// interface + CapturedFrame type
