namespace Cinder.Search;

/// <summary>A geolocated artifact event — EXIF GPS, Wi-Fi, browser geo, IP geolocation.</summary>
public sealed record GeoPoint(
    double Latitude,
    double Longitude,
    DateTimeOffset? Timestamp,
    string Label,
    string Source,
    string? User);

public sealed class GeoIndex
{
    private readonly List<GeoPoint> _points = new();
    public int Count => _points.Count;
    public void Add(GeoPoint p) => _points.Add(p);
    public IReadOnlyList<GeoPoint> AllPoints => _points;

    public IEnumerable<GeoPoint> InBounds(double southWestLat, double southWestLng, double northEastLat, double northEastLng)
        => _points.Where(p => p.Latitude >= southWestLat && p.Latitude <= northEastLat
                           && p.Longitude >= southWestLng && p.Longitude <= northEastLng);
}
