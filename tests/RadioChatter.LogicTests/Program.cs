using System;
using RadioChatter.Comms;

internal static class Program
{
    private static int Main()
    {
        Assert(VoiceIntentParser.ContainsSpokenCallsign("Anvil one, copy", "Anvil 1"), "digit words match digits");
        Assert(VoiceIntentParser.ContainsSpokenCallsign(
            "Anvil, this is Broadsword one one, inbound, hold on", "Anvil"), "natural reply contains ground callsign stem");
        Assert(VoiceIntentParser.ContainsSpokenCallsign(
            "Anvil, this is Broadsword one one, inbound, hold on", "Broadsword 1-1"), "natural reply contains player callsign");
        Assert(!VoiceIntentParser.ContainsSpokenCallsign(
            "Anvil, inbound, hold on", "Broadsword 1-1"), "callsign-free reply cannot identify the player");
        VoiceIntent groundReply = VoiceIntentParser.Parse(
            "Anvil, this is Broadsword one one, inbound, hold on", "Overwatch", "Broadsword 1-1");
        Assert(groundReply.Station == VoiceStation.Unspecified, "direct ground reply is not misrouted to Tower or AWACS");
        Assert(VoiceIntentParser.ContainsSpokenCallsign("we accept Sentinel 2", "Sentinel 2"), "callsign matches in a sentence");
        Assert(!VoiceIntentParser.ContainsSpokenCallsign("Anvil ten, copy", "Anvil 1"), "callsign uses token boundaries");
        Assert(!VoiceIntentParser.ContainsSpokenCallsign("Hammer one, copy", "Anvil 1"), "different callsign does not match");
        Assert(VoiceIntentParser.IsGroundSupportDecline("Hammer four, unable"),
            "bare ground callsign plus unable is a decline");
        Assert(VoiceIntentParser.IsGroundSupportDecline("negative Anvil one, cannot assist"),
            "natural negative support wording is a decline");
        Assert(!VoiceIntentParser.IsGroundSupportDecline("Hammer four, this is Broadsword one one, inbound"),
            "normal support acceptance is not a decline");
        Assert(!VoiceIntentParser.IsGroundSupportDecline("Overwatch, Broadsword one one, request picture"),
            "existing AWACS command is not a decline");

        VoiceIntent existing = VoiceIntentParser.Parse(
            "Overwatch, Falcon one one, request picture", "Overwatch", "Falcon 1-1");
        Assert(existing.Kind == VoiceIntentKind.RequestPicture, "existing picture intent remains intact");
        Assert(existing.Station == VoiceStation.Awacs && existing.StationAddressed, "existing AWACS routing remains intact");

        VoiceIntent radioQuiet = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, radio quiet", "Overwatch", "Broadsword 1-1");
        Assert(radioQuiet.Kind == VoiceIntentKind.RequestAwacsQuiet,
            "radio quiet selects explicit AWACS quiet mode");
        Assert(radioQuiet.Station == VoiceStation.Awacs && radioQuiet.StationAddressed && radioQuiet.CallsignSpoken,
            "radio quiet retains proper AWACS address and player callsign");

        VoiceIntent resumeCalls = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, resume calls", "Overwatch", "Broadsword 1-1");
        Assert(resumeCalls.Kind == VoiceIntentKind.RequestAwacsResume,
            "resume calls restores routine AWACS traffic");
        VoiceIntent cancelQuiet = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, cancel radio quiet", "Overwatch", "Broadsword 1-1");
        Assert(cancelQuiet.Kind == VoiceIntentKind.RequestAwacsResume,
            "cancel radio quiet resolves as resume rather than quiet");
        Assert(cancelQuiet.Callsign == "Broadsword 1-1",
            "cancel keyword does not leak into the spoken callsign");
        VoiceIntent stopAwacsCalls = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, stop AWACS calls", "Overwatch", "Broadsword 1-1");
        Assert(stopAwacsCalls.Kind == VoiceIntentKind.RequestAwacsQuiet,
            "broader stop-AWACS wording also selects quiet mode");

        VoiceIntent plainVector = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, request vector", "Overwatch", "Broadsword 1-1");
        Assert(plainVector.Kind == VoiceIntentKind.RequestVector,
            "plain vector remains the existing selected-target request");

        VoiceIntent namedGroundVector = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, request vector to Anvil", "Overwatch", "Broadsword 1-1");
        Assert(namedGroundVector.Kind == VoiceIntentKind.RequestVector,
            "named ground vectors remain available to the director's dynamic callsign resolver");
        Assert(VoiceIntentParser.ContainsSpokenCallsign(
            "Overwatch, Broadsword one one, request vector to Anvil one", "to Anvil 1"),
            "numbered ground-vector destination survives speech digit normalization");

        VoiceIntent lastSupportVector = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, request vector to last support request", "Overwatch", "Broadsword 1-1");
        Assert(lastSupportVector.Kind == VoiceIntentKind.RequestVectorGroundSupport,
            "last support request has an explicit ground-vector intent");

        VoiceIntent secondaryVector = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, request vector to secondary", "Overwatch", "Broadsword 1-1");
        Assert(secondaryVector.Kind == VoiceIntentKind.RequestVectorGroundSupport,
            "secondary has an explicit ground-vector intent");

        Console.WriteLine("RadioChatter logic tests passed.");
        return 0;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Assertion failed: " + message);
    }
}
