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

        var supportThreats = new GroundSupportThreatTracker();
        supportThreats.Track(101);
        supportThreats.Track(102);
        Assert(!supportThreats.MarkDestroyed(101) && supportThreats.Count == 1,
            "destroying one of two ground attackers keeps the support task active");
        Assert(supportThreats.MarkDestroyed(102) && supportThreats.Count == 0,
            "destroying the last tracked ground attacker completes the support task");
        Assert(!supportThreats.MarkDestroyed(999),
            "an unrelated destroyed unit cannot complete a support task");
        supportThreats.Restart(201);
        Assert(supportThreats.Count == 1 && supportThreats.MarkDestroyed(201),
            "a new engagement replaces stale attacker tracking for the persistent group");
        Assert(GroundSupportHailGate.ShouldHold(true, true, false),
            "ground support hails stay muted while the player is on the ground");
        Assert(GroundSupportHailGate.ShouldHold(null, false, false),
            "ground support hails wait for a stable airborne determination");
        Assert(GroundSupportHailGate.ShouldHold(false, false, true),
            "ground support hails stay behind the AWACS startup gate after liftoff");
        Assert(!GroundSupportHailGate.ShouldHold(false, false, false),
            "ground support hails release only after confirmed airborne state and AWACS startup release");
        Assert(Math.Abs(GroundSupportHailGate.NextAllowedAt(100f) - 120f) < 0.001f,
            "ground support hail filter blocks all further requests for twenty seconds");
        var supportPlaybackGate = new GroundSupportPlaybackGate();
        Assert(supportPlaybackGate.CanStart(100f),
            "the first ground support hail may begin playback immediately");
        supportPlaybackGate.MarkStarted(100f);
        Assert(!supportPlaybackGate.CanStart(119.999f),
            "a second audible ground support hail stays blocked inside the twenty-second window");
        Assert(supportPlaybackGate.CanStart(120f),
            "a second audible ground support hail releases at the twenty-second boundary");
        supportPlaybackGate.MarkStarted(125f);
        Assert(!supportPlaybackGate.CanStart(144.999f) && supportPlaybackGate.CanStart(145f),
            "the audible cooldown follows the actual playback start rather than an earlier dispatch");
        Assert(!AircraftSessionPolicy.ShouldResetFromHudEvent(42, 42, 42),
            "rebinding the HUD to the same aircraft preserves Winchester and other cockpit state");
        Assert(!AircraftSessionPolicy.ShouldResetFromHudEvent(42, 0, 42),
            "a transient HUD aircraft clear does not reset cockpit state");
        Assert(!AircraftSessionPolicy.ShouldResetFromHudEvent(42, 99, 42),
            "a stale HUD aircraft event cannot override the authoritative snapshot");
        Assert(AircraftSessionPolicy.ShouldResetFromHudEvent(42, 99, 99),
            "a snapshot-confirmed new airframe resets aircraft-specific cockpit state");
        Assert(FlightExitPolicy.IsNormalAirportExit(true, true, false, false),
            "a successful sortie remains a normal exit when the parked aircraft is disabled during despawn");
        Assert(FlightExitPolicy.IsNormalAirportExit(false, false, true, true),
            "a grounded aircraft near its airbase is also a normal exit before the sortie event arrives");
        Assert(!FlightExitPolicy.IsNormalAirportExit(false, true, true, true),
            "a destroyed aircraft without a successful sortie still produces an ejection mayday");
        Assert(!FlightExitPolicy.IsNormalAirportExit(false, false, false, true),
            "an airborne exit without a successful sortie is not classified as a normal airport exit");
        float retryAwaitingSince = 100f;
        retryAwaitingSince = TowerReadbackTiming.RefreshAwaitingSince(retryAwaitingSince, 109f, true);
        Assert(Math.Abs(retryAwaitingSince - 109f) < 0.001f,
            "Tower correction audio refreshes the retry response window");
        Assert(!TowerReadbackTiming.HasTimedOut(retryAwaitingSince, 118.999f, 10f),
            "the corrected readback remains eligible for the full response window after Tower finishes");
        Assert(TowerReadbackTiming.HasTimedOut(retryAwaitingSince, 119f, 10f),
            "a missing corrected readback times out only after the refreshed response window");
        Assert(!CarrierCommsPolicy.IsCarrierAirbase(false, false, false),
            "a conventional fixed airbase keeps runway and Tower phraseology");
        Assert(CarrierCommsPolicy.IsCarrierAirbase(true, false, false) &&
               CarrierCommsPolicy.IsCarrierAirbase(false, true, false) &&
               CarrierCommsPolicy.IsCarrierAirbase(false, false, true),
            "attached airbases, ski jumps, and arrestors each identify carrier operations");
        Assert(CarrierCommsPolicy.TakeoffPhraseKey(true) == "carrier_launch" &&
               CarrierCommsPolicy.FinalPhraseKey(true) == "carrier_recovery" &&
               CarrierCommsPolicy.TakeoffPhraseKey(false) == "tower_takeoff",
            "carrier and land-base phrase routing remain distinct");
        Assert(TowerReadbackMatcher.TryCreate(
                   "Broadsword 1-1, deck control, cleared for launch", out TowerReadbackExpectation launchExpectation) &&
               launchExpectation.Kind == TowerReadbackKind.Takeoff &&
               TowerReadbackMatcher.IsMatch("cleared for launch, Broadsword one one", launchExpectation),
            "a carrier launch clearance accepts a launch readback without a runway");
        Assert(TowerReadbackMatcher.TryCreate(
                   "Broadsword 1-1, carrier, cleared for recovery", out TowerReadbackExpectation recoveryExpectation) &&
               recoveryExpectation.Kind == TowerReadbackKind.Landing &&
               TowerReadbackMatcher.IsMatch("cleared to recover, Broadsword one one", recoveryExpectation),
            "a carrier recovery clearance accepts recovery phraseology without a runway");
        VoiceIntent deckLaunch = VoiceIntentParser.Parse(
            "Deck control, Broadsword one one, request launch", "Darkstar", "Broadsword 1-1");
        Assert(deckLaunch.Kind == VoiceIntentKind.RequestTakeoff &&
               deckLaunch.Station == VoiceStation.Tower && deckLaunch.StationAddressed && deckLaunch.CallsignSpoken,
            "deck-control launch requests route through the Tower command channel");
        VoiceIntent carrierRecovery = VoiceIntentParser.Parse(
            "Carrier, Broadsword one one, request recovery", "Darkstar", "Broadsword 1-1");
        Assert(carrierRecovery.Kind == VoiceIntentKind.RequestLanding &&
               carrierRecovery.Station == VoiceStation.Tower && carrierRecovery.StationAddressed,
            "carrier recovery requests route through the Tower command channel");
        VoiceIntent awacsSayAgain = VoiceIntentParser.Parse(
            "Darkstar, this is Broadsword one one, say again", "Darkstar", "Broadsword 1-1");
        Assert(awacsSayAgain.Kind == VoiceIntentKind.RequestRepeatLast &&
               awacsSayAgain.Station == VoiceStation.Awacs && awacsSayAgain.CallsignSpoken,
            "a properly addressed AWACS say-again request selects station replay");
        VoiceIntent towerPleaseRepeat = VoiceIntentParser.Parse(
            "Tower, Broadsword one one, please repeat", "Darkstar", "Broadsword 1-1");
        Assert(towerPleaseRepeat.Kind == VoiceIntentKind.RequestRepeatLast &&
               towerPleaseRepeat.Station == VoiceStation.Tower,
            "please-repeat wording selects the addressed Tower history");
        VoiceIntent repeatYourLast = VoiceIntentParser.Parse(
            "Darkstar, Broadsword one one, repeat your last", "Darkstar", "Broadsword 1-1");
        Assert(repeatYourLast.Kind == VoiceIntentKind.RequestRepeatLast,
            "repeat-your-last wording selects station replay");
        var stationHistory = new StationTransmissionHistory();
        stationHistory.Record(VoiceStation.Tower, "Broadsword 1-1, cleared for takeoff runway three right");
        stationHistory.Record(VoiceStation.Awacs, "Broadsword 1-1, Darkstar, picture clean");
        Assert(stationHistory.TryGet(VoiceStation.Tower, out string lastTower) &&
               lastTower.IndexOf("cleared for takeoff", StringComparison.Ordinal) >= 0,
            "Tower repeat history remains independent from AWACS");
        Assert(stationHistory.TryGet(VoiceStation.Awacs, out string lastAwacs) &&
               lastAwacs.IndexOf("picture clean", StringComparison.Ordinal) >= 0,
            "AWACS repeat history returns that station's last transmission");

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

        VoiceIntent terseWinchester = VoiceIntentParser.Parse(
            "Winchester to AWACS", "Overwatch", "Broadsword 1-1");
        Assert(terseWinchester.Kind == VoiceIntentKind.DeclareWinchester,
            "terse Winchester-to-AWACS wording declares weapons depleted");
        Assert(terseWinchester.Station == VoiceStation.Awacs && terseWinchester.Callsign.Length == 0,
            "terse Winchester declaration routes to AWACS without treating Winchester as a callsign");
        VoiceIntent formalWinchester = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, Winchester", "Overwatch", "Broadsword 1-1");
        Assert(formalWinchester.Kind == VoiceIntentKind.DeclareWinchester &&
               formalWinchester.StationAddressed && formalWinchester.CallsignSpoken,
            "formal Winchester declaration retains proper AWACS address and callsign");

        VoiceIntent seadCheckIn = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, checking in, SEAD as fragged", "Overwatch", "Broadsword 1-1");
        Assert(seadCheckIn.Kind == VoiceIntentKind.SetMissionRole &&
               seadCheckIn.MissionRole == FlightMissionRole.Sead,
            "SEAD mission check-in selects the SEAD role");
        Assert(seadCheckIn.Callsign == "Broadsword 1-1" && seadCheckIn.StationAddressed,
            "mission check-in retains proper station and callsign format");

        VoiceIntent capCheckIn = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, checking in as CAP", "Overwatch", "Broadsword 1-1");
        Assert(capCheckIn.Kind == VoiceIntentKind.SetMissionRole &&
               capCheckIn.MissionRole == FlightMissionRole.Cap,
            "CAP mission check-in selects the CAP role");

        VoiceIntent casCheckIn = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, mission C A S", "Overwatch", "Broadsword 1-1");
        Assert(casCheckIn.Kind == VoiceIntentKind.SetMissionRole &&
               casCheckIn.MissionRole == FlightMissionRole.Cas,
            "spoken-letter CAS mission selects the CAS role");

        VoiceIntent strikeCheckIn = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, strike as fragged", "Overwatch", "Broadsword 1-1");
        Assert(strikeCheckIn.Kind == VoiceIntentKind.SetMissionRole &&
               strikeCheckIn.MissionRole == FlightMissionRole.Strike,
            "strike as fragged selects the strike role");

        VoiceIntent bombingMission = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, checking in, bombing mission", "Overwatch", "Broadsword 1-1");
        Assert(bombingMission.Kind == VoiceIntentKind.SetMissionRole &&
               bombingMission.MissionRole == FlightMissionRole.Strike && bombingMission.CallsignSpoken,
            "bombing mission is accepted as the strike/air-interdiction role without corrupting the callsign");

        VoiceIntent airInterdictionMission = VoiceIntentParser.Parse(
            "mission air interdiction", "Overwatch", "Broadsword 1-1");
        Assert(airInterdictionMission.Kind == VoiceIntentKind.SetMissionRole &&
               airInterdictionMission.MissionRole == FlightMissionRole.Strike,
            "air interdiction is accepted as doctrinal strike tasking");

        VoiceIntent maritimeStrikeMission = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, maritime strike as fragged", "Overwatch", "Broadsword 1-1");
        Assert(maritimeStrikeMission.Kind == VoiceIntentKind.SetMissionRole &&
               maritimeStrikeMission.MissionRole == FlightMissionRole.MaritimeStrike &&
               maritimeStrikeMission.CallsignSpoken,
            "maritime strike selects the ship-attack role without corrupting the callsign");

        VoiceIntent antiSurfaceMission = VoiceIntentParser.Parse(
            "mission anti surface warfare", "Overwatch", "Broadsword 1-1");
        Assert(antiSurfaceMission.Kind == VoiceIntentKind.SetMissionRole &&
               antiSurfaceMission.MissionRole == FlightMissionRole.MaritimeStrike,
            "anti-surface warfare is accepted as a maritime-strike alias");

        VoiceIntent searchAndDestroyMission = VoiceIntentParser.Parse(
            "mission search and destroy", "Overwatch", "Broadsword 1-1");
        Assert(searchAndDestroyMission.Kind == VoiceIntentKind.SetMissionRole &&
               searchAndDestroyMission.MissionRole == FlightMissionRole.SearchAndDestroy,
            "terse mission search and destroy selects the search-and-destroy role");
        Assert(VoiceIntentParser.ContainsMissionCommandWord("mission search and destroy"),
            "terse mission role wording is recognized as an explicit cockpit command");

        VoiceIntent generalMission = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, mission general", "Overwatch", "Broadsword 1-1");
        Assert(generalMission.Kind == VoiceIntentKind.SetMissionRole &&
               generalMission.MissionRole == FlightMissionRole.None,
            "mission general clears role filtering");

        VoiceIntent plainCheckIn = VoiceIntentParser.Parse(
            "Overwatch, Broadsword one one, checking in", "Overwatch", "Broadsword 1-1");
        Assert(plainCheckIn.Kind == VoiceIntentKind.CheckIn,
            "check-in without a mission preserves the existing all-chatter behavior");
        Assert(FlightMissionRolePolicy.SuppressGroundSupportHails(FlightMissionRole.Cap) &&
               !FlightMissionRolePolicy.SuppressAutomaticAirContacts(FlightMissionRole.Cap),
            "CAP keeps air contacts and suppresses ground hails");
        Assert(!FlightMissionRolePolicy.SuppressGroundSupportHails(FlightMissionRole.Cas) &&
               FlightMissionRolePolicy.SuppressAutomaticAirContacts(FlightMissionRole.Cas),
            "CAS keeps ground hails and suppresses air contacts");
        Assert(FlightMissionRolePolicy.SuppressGroundSupportHails(FlightMissionRole.Sead) &&
               FlightMissionRolePolicy.SuppressAutomaticAirContacts(FlightMissionRole.Sead),
            "SEAD suppresses both generic chatter streams");
        Assert(FlightMissionRolePolicy.SuppressGroundSupportHails(FlightMissionRole.Strike) &&
               !FlightMissionRolePolicy.SuppressAutomaticAirContacts(FlightMissionRole.Strike),
            "strike suppresses ground hails and retains air contacts");
        Assert(FlightMissionRolePolicy.SuppressGroundSupportHails(FlightMissionRole.MaritimeStrike) &&
               !FlightMissionRolePolicy.SuppressAutomaticAirContacts(FlightMissionRole.MaritimeStrike),
            "maritime strike suppresses ground hails and retains defensive air contacts");
        Assert(FlightMissionRolePolicy.SuppressGroundSupportHails(FlightMissionRole.SearchAndDestroy) &&
               !FlightMissionRolePolicy.SuppressAutomaticAirContacts(FlightMissionRole.SearchAndDestroy),
            "search and destroy uses strike-like chatter filtering");
        Assert(!FlightMissionRolePolicy.SuppressGroundSupportHails(FlightMissionRole.None) &&
               !FlightMissionRolePolicy.SuppressAutomaticAirContacts(FlightMissionRole.None),
            "no mission role preserves all chatter");

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
