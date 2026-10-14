namespace Kotoshiro;

internal static class PlaybackGateTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL: " + message);
    }

    internal static List<string> Run()
    {
        var results = new List<string>();
        var network = new PlaybackGate(false);
        Check(!network.Update(10, 10.5, 0, false).Hold, "network boundary remains playable at half a second");
        Check(network.Update(10, 10.4, 0, false) == new PlaybackHold(true, false), "network underrun pauses independently of subtitles");
        Check(network.Update(10, 11.9, 0, false).Network, "network does not resume with less than two seconds buffered");
        Check(!network.Update(10, 12, 0, false).Hold, "network resumes with two seconds buffered");
        Check(!network.Update(10, 10.8, 0, false).Hold, "network remains playing within the recovery margin");
        Check(network.Update(10, 10.4, 0, false).Network, "a later genuine underrun pauses again");
        Check(!network.Update(10, 10.4, 0, true).Hold, "finished input releases a short buffered tail");
        Check(!network.Update(10.4, 10.4, 0, true).Hold, "finished input can reach EOF without a network hold");
        results.Add("PASS: independent network pause/resume hysteresis, recovery boundary, repeat underrun and EOF tail release");

        var subtitles = new PlaybackGate(true);
        Check(!subtitles.Update(5, 20, 5.2, false).Hold, "a playing subtitle lead above the stop threshold continues");
        Check(subtitles.Update(5, 20, 5, false) == new PlaybackHold(false, true), "subtitle boundary pauses while later subtitles remain pending");
        Check(subtitles.Update(5, 20, 6.9, false).Subtitles, "subtitle hold waits for two seconds of completed translations");
        Check(!subtitles.Update(5, 20, 7, false).Hold, "subtitle hold resumes at two seconds of ready text");
        Check(!subtitles.Update(6, 20, 7, false).Hold, "playing does not stop again inside the recovery margin");
        Check(subtitles.Update(7, 20, 7, false).Subtitles, "next missing subtitle boundary holds again");
        Check(!subtitles.Update(7, 8, 8, true).Hold, "all completed short final captions release the tail below two seconds");
        Check(!subtitles.Update(8, 8, 8, true).Hold, "completed final captions permit EOF");
        results.Add("PASS: subtitle boundary hold, two-second recovery, continued play within the margin and completed short tail release");

        var unfinishedTail = new PlaybackGate(true);
        Check(unfinishedTail.Update(7, 8, 7, true).Subtitles, "EOF alone does not discard a pending final translation");
        Check(!unfinishedTail.Update(7, 8, 8, true).Hold, "the final translation releases playback after EOF input");
        var completedLive = new PlaybackGate(true);
        Check(completedLive.Update(7, 8, 7, false).Subtitles, "untranslated live tail waits");
        Check(!completedLive.Update(7, 8, 8, false).Hold, "fully translated live tail can resume without waiting for another segment");
        results.Add("PASS: pending final translation survives input EOF, completed live tail and completed EOF tail both resume");

        var disabled = new PlaybackGate(false);
        Check(!disabled.Update(10, 20, 0, false).Hold, "wait-disabled mode never holds missing subtitles");
        Check(disabled.Update(10, 10.4, 0, false) == new PlaybackHold(true, false), "wait-disabled mode still protects video buffer");
        var combined = new PlaybackGate(true);
        Check(combined.Update(10, 10.4, 9, false) == new PlaybackHold(true, true), "network and subtitle shortages are distinguishable");
        Check(combined.Update(10, 12, 10, false) == new PlaybackHold(false, true), "network recovery does not release pending subtitles");
        Check(!combined.Update(10, 14, 12, false).Hold, "both recovered causes release playback");
        results.Add("PASS: wait-disabled behavior and independent network/subtitle shortage causes");

        Check(PlaybackGate.TargetDelay(3, 6, true) == 8, "low requested delay leaves room for a complete recognition segment");
        Check(PlaybackGate.TargetDelay(10, 6, true) == 10, "an adequate requested delay is preserved");
        Check(PlaybackGate.TargetDelay(3, 6, false) == 3, "wait-disabled startup preserves the user's short delay");
        Check(PlaybackGate.TargetDelay(0, 0, false) == 2 && PlaybackGate.TargetDelay(70, 6, true) == 60 &&
            PlaybackGate.TargetDelay(3, int.MaxValue, true) == 60, "startup delay is bounded without integer overflow");
        results.Add("PASS: startup delay resolves low-delay/recognition-segment conflict and retains the requested delay when adequate or waiting is disabled");
        return results;
    }
}
