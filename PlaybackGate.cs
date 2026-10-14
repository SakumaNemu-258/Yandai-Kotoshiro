namespace Kotoshiro;

/// <summary>Independent recovery margins prevent playback from repeatedly stopping at a buffer boundary.</summary>
internal sealed class PlaybackGate(bool waitForSubtitles)
{
    private bool networkHold, subtitleHold;

    internal PlaybackHold Update(double position, double edge, double resolvedThrough, bool inputFinished)
    {
        double bufferedAhead = edge - position, subtitlesAhead = resolvedThrough - position;
        networkHold = !inputFinished && bufferedAhead < (networkHold ? 2 : 0.5);

        bool pendingSubtitles = resolvedThrough < edge - 0.1;
        subtitleHold = waitForSubtitles && pendingSubtitles &&
            (subtitleHold ? subtitlesAhead < 2 : subtitlesAhead <= 0.15);
        return new(networkHold, subtitleHold);
    }

    internal static int TargetDelay(int requested, int segment, bool wait)
        => (int)Math.Clamp(wait ? Math.Max((long)requested, (long)segment + 2) : requested, 2L, 60L);
}

internal readonly record struct PlaybackHold(bool Network, bool Subtitles)
{
    internal bool Hold => Network || Subtitles;
}
