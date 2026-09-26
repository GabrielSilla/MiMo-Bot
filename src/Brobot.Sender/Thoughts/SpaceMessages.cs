using System.Net.Http;
using SGPdotNET.CoordinateSystem;
using SGPdotNET.Observation;
using SGPdotNET.Propagation.Bodies;
using SGPdotNET.Util;

namespace Brobot.Sender.Thoughts;

/// <summary>
/// "Um satélite famoso vai passar no seu céu" — the one priority source
/// (see specs/sender-thoughts.md, "SpaceMessages"): asked first at every
/// slot, and only answers when a real, naked-eye-visible pass of the ISS,
/// Tiangong or Hubble is happening or starts within the next few hours.
/// Computed locally with SGP4 (SGP.NET) from CelesTrak's free orbital
/// elements, fetched at most once a day and cached on disk
/// (tle-cache.json) — no API key anywhere (N2YO was rejected for needing
/// one). Always shown as NOTIFY SATELLITE.
/// </summary>
internal sealed class SpaceMessages : IThoughtSource, IBackgroundThoughtSource
{
    private const string CacheFile = "tle-cache.json";
    private static readonly TimeSpan TleMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan RefreshCheckInterval = TimeSpan.FromHours(1);

    // Only famous ones: something generic passes overhead every few minutes,
    // which would kill the magic. Article + name as it reads mid-sentence;
    // Crewed decides whether "dá um tchau, tem gente lá" makes sense.
    private sealed record Target(int CatalogNumber, string Name, bool Crewed);
    private static readonly Target[] Targets =
    {
        new(25544, "a ISS", Crewed: true),
        new(48274, "a Tiangong", Crewed: true),
        new(20580, "o Hubble", Crewed: false),
    };

    private const double MinMaxElevationDeg = 30;   // low passes are hard to see and don't impress
    private const double HorizonDeg = 10;           // where a pass "starts" for the direction to look
    private const double DarkSunElevationDeg = -6;  // civil dusk: dark enough to spot a satellite
    private const double EarthRadiusKm = 6378.137;
    private static readonly TimeSpan LookAhead = TimeSpan.FromHours(3);

    private static readonly string[] UpcomingTemplates =
    {
        "{Sat} passa às {hora} bem em cima de você. Olha pro {dir}!",
        "Hoje às {hora}, {sat} cruza seu céu. Começa pelo {dir}.",
        "{Sat} vai passar visível às {hora}. Procura no {dir} do céu.",
        "Anota aí: {hora}, {sat} aparece pelo {dir}. Um ponto andando.",
        "Encontro marcado às {hora}: {sat} no seu céu, lado {dir}.",
        "Olha pro {dir} às {hora}. {Sat} vai estar passando bem visível.",
        "{Sat} vem aí às {hora}. Olha pro {dir} e procura um ponto.",
        "Hoje tem {sat} no céu às {hora}. Sobe pelo {dir}.",
    };
    private static readonly string[] UpcomingCrewedTemplates =
    {
        "{Sat} passa às {hora} pelo {dir}. Tem gente lá dentro, dá um tchau.",
        "Acena pro {dir} às {hora}: {sat} passa, e tem astronauta lá.",
    };
    private static readonly string[] NowTemplates =
    {
        "{Sat} tá passando em cima de você agora. Olha pro {dir}!",
        "Agora mesmo {sat} tá cruzando seu céu. Corre, lado {dir}!",
        "{Sat} tá lá em cima agora, pro lado {dir}. Corre que dá tempo.",
        "Neste instante {sat} passa por cima de você. Olha pro {dir}!",
    };
    private static readonly string[] NowCrewedTemplates =
    {
        "{Sat} tá passando agora, lado {dir}. Dá um tchau, tem gente lá.",
    };

    private static readonly string[] CompassPt =
        { "norte", "nordeste", "leste", "sudeste", "sul", "sudoeste", "oeste", "noroeste" };

    public sealed class TleCache
    {
        public DateTime FetchedAtUtc { get; set; }
        public Dictionary<int, string[]> Tles { get; set; } = new();
    }

    /// <summary>A visible pass, in UTC; Direction is where to look when it rises above HorizonDeg.</summary>
    public sealed record Pass(int CatalogNumber, string Name, bool Crewed, DateTime StartUtc, DateTime EndUtc,
                              double MaxElevationDeg, string Direction)
    {
        public string Id => $"pass:{CatalogNumber}:{StartUtc:yyyyMMddHHmm}";
    }

    private readonly ThoughtHistoryStore _history;
    private readonly object _lock = new();
    private readonly Random _rng = new();
    private TleCache _tles;

    public string Name => "space";
    public double Weight => 0;
    public bool IsPriority => true;

    public SpaceMessages(ThoughtHistoryStore history)
    {
        _history = history;
        _tles = ThoughtHttp.LoadJson<TleCache>(CacheFile) ?? new TleCache();
    }

    public Thought? TryPick(ThoughtContext context)
    {
        if (_history.CountToday(Name) >= 1)
        {
            return null; // at most one space thought a day
        }

        DateTime nowUtc = context.Now.ToUniversalTime();
        Pass? pass = FindPasses(nowUtc, nowUtc + LookAhead)
            .Where(p => !_history.WasFiredToday(p.Id))
            .OrderBy(p => p.StartUtc)
            .FirstOrDefault();
        if (pass == null)
        {
            return null;
        }

        _history.MarkFired(pass.Id);
        return ToThought(pass, nowUtc);
    }

    /// <summary>
    /// Modo teste: the next qualifying pass in the next two days, ignoring
    /// the 3h window and the one-a-day limit, so the text can be checked on
    /// the device without waiting. Null with a reason when there's none.
    /// </summary>
    public (Thought? Thought, string? Why) ForceNextPass(DateTime now)
    {
        if (LocationProvider.Cached == null) return (null, "sem localização (permita em Config. do Windows > Privacidade > Localização)");
        lock (_lock)
        {
            if (_tles.Tles.Count == 0) return (null, "órbitas ainda não baixadas do CelesTrak");
        }

        DateTime nowUtc = now.ToUniversalTime();
        Pass? pass = FindPasses(nowUtc, nowUtc + TimeSpan.FromHours(48)).OrderBy(p => p.StartUtc).FirstOrDefault();
        return pass == null
            ? (null, "nenhuma passagem visível nas próximas 48h")
            : (ToThought(pass, nowUtc), null);
    }

    public void Start() => _ = RefreshLoopAsync();

    private Thought ToThought(Pass pass, DateTime nowUtc)
    {
        bool happeningNow = pass.StartUtc <= nowUtc;
        string[] pool = happeningNow
            ? (pass.Crewed ? NowTemplates.Concat(NowCrewedTemplates).ToArray() : NowTemplates)
            : (pass.Crewed ? UpcomingTemplates.Concat(UpcomingCrewedTemplates).ToArray() : UpcomingTemplates);
        string template = pool[_rng.Next(pool.Length)];
        string text = template
            .Replace("{Sat}", char.ToUpperInvariant(pass.Name[0]) + pass.Name[1..])
            .Replace("{sat}", pass.Name)
            .Replace("{hora}", pass.StartUtc.ToLocalTime().ToString("HH:mm"))
            .Replace("{dir}", pass.Direction);
        return new Thought("SATELLITE", text, Name, "passagem", pass.Id);
    }

    private List<Pass> FindPasses(DateTime fromUtc, DateTime toUtc)
    {
        var passes = new List<Pass>();
        LocationProvider.Result? location = LocationProvider.Cached;
        Dictionary<int, string[]> tles;
        lock (_lock)
        {
            tles = new Dictionary<int, string[]>(_tles.Tles);
        }
        if (location == null || tles.Count == 0)
        {
            return passes;
        }

        var station = new GroundStation(new GeodeticCoordinate(
            Angle.FromDegrees(location.Latitude), Angle.FromDegrees(location.Longitude), 0));

        foreach (Target target in Targets)
        {
            if (!tles.TryGetValue(target.CatalogNumber, out string[]? tle) || tle.Length != 3)
            {
                continue;
            }
            try
            {
                var satellite = new Satellite(tle[0], tle[1], tle[2]);
                // Start a little in the past so a pass already under way is
                // found too ("tá passando agora").
                List<SatelliteVisibilityPeriod> periods = station.Observe(
                    satellite, fromUtc - TimeSpan.FromMinutes(15), toUtc, TimeSpan.FromSeconds(10),
                    Angle.FromDegrees(HorizonDeg), clipToStartTime: false);

                foreach (SatelliteVisibilityPeriod period in periods)
                {
                    if (period.End < fromUtc || period.MaxElevation.Degrees < MinMaxElevationDeg)
                    {
                        continue;
                    }
                    if (IsNakedEyeVisible(station, satellite, period.Start, period.End))
                    {
                        double azimuth = station.Observe(satellite, period.Start).Azimuth.Degrees;
                        passes.Add(new Pass(target.CatalogNumber, target.Name, target.Crewed, period.Start, period.End,
                                            period.MaxElevation.Degrees, CompassPt[(int)Math.Round(azimuth / 45.0) % 8]));
                    }
                }
            }
            catch (Exception)
            {
                // A malformed/stale TLE for one satellite shouldn't hide the others.
            }
        }
        return passes;
    }

    /// <summary>
    /// "Passing overhead" is only worth saying if you can actually see it:
    /// dark enough where the user is (sun below -6°) while the satellite
    /// itself is still in sunlight (not in Earth's shadow) at some point of
    /// the pass. Earth's shadow is modeled as a cylinder — plenty for a yes/no.
    /// </summary>
    private static bool IsNakedEyeVisible(GroundStation station, Satellite satellite, DateTime startUtc, DateTime endUtc)
    {
        for (DateTime t = startUtc; t <= endUtc; t += TimeSpan.FromSeconds(20))
        {
            EciCoordinate sun = Sun.Predict(t);
            if (station.Observe(sun, t).Elevation.Degrees > DarkSunElevationDeg)
            {
                continue;
            }
            if (IsSunlit(satellite.Predict(t).Position, sun.Position))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSunlit(Vector3 sat, Vector3 sun)
    {
        double sunLength = Math.Sqrt(sun.X * sun.X + sun.Y * sun.Y + sun.Z * sun.Z);
        double ux = sun.X / sunLength, uy = sun.Y / sunLength, uz = sun.Z / sunLength;
        double along = sat.X * ux + sat.Y * uy + sat.Z * uz;
        if (along >= 0)
        {
            return true; // on the day side of Earth
        }
        double px = sat.X - along * ux, py = sat.Y - along * uy, pz = sat.Z - along * uz;
        return Math.Sqrt(px * px + py * py + pz * pz) > EarthRadiusKm;
    }

    private async Task RefreshLoopAsync()
    {
        // Warm the shared location up front; passes can't be computed without it.
        _ = LocationProvider.GetAsync();

        while (true)
        {
            try
            {
                bool stale;
                lock (_lock)
                {
                    stale = DateTime.UtcNow - _tles.FetchedAtUtc > TleMaxAge || _tles.Tles.Count < Targets.Length;
                }
                if (stale)
                {
                    await FetchTlesAsync();
                }
            }
            catch (Exception)
            {
                // Offline — yesterday's elements stay good enough for a few days.
            }
            await Task.Delay(RefreshCheckInterval);
        }
    }

    private async Task FetchTlesAsync()
    {
        var fresh = new Dictionary<int, string[]>();
        foreach (Target target in Targets)
        {
            string url = $"https://celestrak.org/NORAD/elements/gp.php?CATNR={target.CatalogNumber}&FORMAT=TLE";
            using HttpResponseMessage response = await ThoughtHttp.Client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }
            string[] lines = (await response.Content.ReadAsStringAsync())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length >= 3 && lines[1].StartsWith("1 ") && lines[2].StartsWith("2 "))
            {
                fresh[target.CatalogNumber] = lines[..3];
            }
        }

        if (fresh.Count > 0)
        {
            lock (_lock)
            {
                // Keep yesterday's elements for any satellite whose fetch failed.
                foreach ((int id, string[] tle) in fresh)
                {
                    _tles.Tles[id] = tle;
                }
                _tles.FetchedAtUtc = DateTime.UtcNow;
                ThoughtHttp.SaveJson(CacheFile, _tles);
            }
        }
    }
}
