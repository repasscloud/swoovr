namespace Swoovr.Client;

/// <summary>
/// Issue 282: the web location wording, approved word for word on #271 (D351) §12–§14 (curly quotes set straight). The
/// Privacy Notice 0.21 location section (D432, D433) is what these describe; change them together.
/// </summary>
public static class LocationText
{
    // §12 Web permission messages.
    public const string BeforePrompt = "Share your location to use Near me. Swoovr receives readings while Near me is open and derives limited daily outcomes to help identify an outdated Home City. You can browse without appearing in nearby searches. Read our Privacy Notice for the details.";
    public const string Blocked = "Location is blocked for Swoovr. You can allow it in your browser's site settings, then try again. Home market remains available without location.";
    public const string Unsupported = "This browser cannot share your location. You can continue using Home market.";
    public const string Inaccurate = "Near me needs a location reading accurate to within 2 km. Try again with precise location enabled, or continue using Home market.";

    // §13 Settings descriptions.
    public const string AppearLabel = "Appear in nearby searches";
    public const string AppearHelp = "Let eligible members find you through Near me using your recent rounded location. Turning this off hides you from nearby discovery. You can continue browsing Near me without appearing.";
    public const string DistanceLabel = "Show my approximate distance";
    public const string DistanceHelp = "Let eligible members see an approximate distance band. Your coordinates and exact distance are never shown to them. You can see other members' bands only while this setting is on.";
    public const string StopLabel = "Stop sharing location";
    public const string StopHelp = "Stop sending new Near me readings and delete your stored Near me position. Previously derived Home City check records follow the retention periods in our Privacy Notice.";

    // §14 Travel switch descriptions.
    public const string BeDiscoveredLabel = "Let people there discover me before arrival";
    public const string BeDiscoveredHelp = "Let eligible members at your destination see that you are visiting when the trip's discovery window opens.";
    public const string DiscoverLabel = "Discover people there before arrival";
    public const string DiscoverHelp = "Browse eligible members at your destination when the trip's discovery window opens. Starting contact remains subject to arrival and contact rules.";

    /// <summary>The Near me reading accuracy the API accepts (D348): 2 km.</summary>
    public const double MaxAccuracyMetres = 2000;
    /// <summary>Issue 282: Near me reads again at most every 5 minutes while it is open and the page is visible; never in the background.</summary>
    public static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);
}
