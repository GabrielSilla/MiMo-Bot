using Windows.Devices.Geolocation;

namespace Brobot.Sender;

/// <summary>
/// The machine's approximate location via Windows' own geolocation, looked
/// up once per session and shared — WeatherMonitor needs it for the
/// forecast and Pensamentos' SpaceMessages for satellite passes, and the
/// latter must work even with the Clima card off. City-level accuracy is
/// plenty for both, and avoids the higher-power/higher-friction GPS-grade
/// request.
/// </summary>
public static class LocationProvider
{
    public sealed record Result(double Latitude, double Longitude, string? Error)
    {
        public bool Ok => Error == null;
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Result? _cached;

    /// <summary>The last successful location, if any lookup has succeeded yet (never blocks).</summary>
    public static Result? Cached => _cached is { Ok: true } ? _cached : null;

    /// <summary>
    /// Looks the location up the first time and reuses it afterwards. A
    /// failed lookup isn't cached, so a later call (e.g. after the user
    /// allows location in Windows settings) tries again.
    /// </summary>
    public static async Task<Result> GetAsync()
    {
        if (_cached is { Ok: true } hit)
        {
            return hit;
        }

        await Gate.WaitAsync();
        try
        {
            if (_cached is { Ok: true } again)
            {
                return again;
            }

            GeolocationAccessStatus access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                return new Result(0, 0, "Acesso à localização negado (Config. do Windows > Privacidade > Localização)");
            }

            var locator = new Geolocator { DesiredAccuracyInMeters = 10000 };
            Geoposition position = await locator.GetGeopositionAsync();
            _cached = new Result(position.Coordinate.Point.Position.Latitude,
                                 position.Coordinate.Point.Position.Longitude, null);
            return _cached;
        }
        catch (Exception ex)
        {
            return new Result(0, 0, $"Falha ao obter localização: {ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }
}
