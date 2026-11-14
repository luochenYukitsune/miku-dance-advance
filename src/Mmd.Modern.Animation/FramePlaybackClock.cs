namespace Mmd.Modern.Animation;

/// <summary>Elapsed-time clock independent of a window, render cadence, and pose evaluation.</summary>
public sealed class FramePlaybackClock
{
    public const int FramesPerSecond=30;
    public double Frame {get;private set;}
    public uint DurationFrames {get;private set;}
    public bool IsPlaying {get;private set;}
    public void SetDuration(uint frames){DurationFrames=frames;Frame=0;IsPlaying=false;}
    public void Seek(double frame)
    {
        if(!double.IsFinite(frame))throw new ArgumentOutOfRangeException(nameof(frame));
        Frame=Math.Clamp(frame,0,DurationFrames);
    }
    public void Play(){if(DurationFrames==0)return;if(Frame>=DurationFrames)Frame=0;IsPlaying=true;}
    public void Pause()=>IsPlaying=false;
    public void Advance(TimeSpan elapsed)
    {
        if(elapsed.Ticks<0)throw new ArgumentOutOfRangeException(nameof(elapsed));
        if(!IsPlaying)return;
        Frame=Math.Min(DurationFrames,Frame+elapsed.TotalSeconds*FramesPerSecond);
        if(Frame>=DurationFrames)IsPlaying=false;
    }
}
